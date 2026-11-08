// Tray.cs - the tray app (double-click the exe, or --tray): an icon in the notification area while the monitor runs.
// Grey icon = waiting for the game, colour = watching it; the tooltip says what limits the game in plain words.
// Double-click opens the dashboard; right-click: Open dashboard / Game log / Exit. Exit is a clean shutdown (the frame
// capture's ETW session is stopped). One copy per Windows session: starting it again asks the running copy to open
// its dashboard; `SVSPerfMonitor --exit` asks it to quit. Plain Win32 (Shell_NotifyIcon), no Windows Forms, so the
// app still compiles to one native exe (NativeAOT). Copyright (c) 2026 Jared Brown. MIT licence.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FSPerf;

public static unsafe class TrayApp
{
    private const string Title = "SVS Performance Monitor";
    private const uint WM_TRAY = TrayNative.WM_APP + 1, WM_OPEN_REQUEST = TrayNative.WM_APP + 2, WM_EXIT_REQUEST = TrayNative.WM_APP + 3,
        WM_SETTINGS_SAVED = TrayNative.WM_APP + 4;
    private const int CmdOpen = 1, CmdLog = 2, CmdSettings = 3, CmdExit = 9;
    private const nuint TimerId = 1, RestartTimerId = 2;

    private static MonitorHost? s_host;
    private static IntPtr s_hwnd, s_iconWatching, s_iconWaiting;
    private static uint s_taskbarCreated;
    private static bool s_iconAdded, s_watching, s_shutDown;
    private static string s_tip = "", s_status = "Waiting for the game";

    private static string OpenEventName(string instance) => $@"Local\{instance}.Open";
    private static string ExitEventName(string instance) => $@"Local\{instance}.Exit";
    private static string MutexName(string instance) => $@"Local\{instance}.Running";

    private static string[] s_args = [];
    private static AppSettings s_settings = new();
    private static readonly object s_settingsLock = new();   // saves come from HTTP worker threads, the fallback from the UI
    private static readonly SettingsBackend s_backend = new();

    // the Settings page's side in the tray app: save to the file, then restart the monitor with them (on the window's thread)
    private sealed class SettingsBackend : ISettingsBackend
    {
        public AppSettings Current { get { lock (s_settingsLock) return s_settings.Clone(); } }
        public string? Save(AppSettings s)
        {
            s = s.Clone().Normalized();
            string? problem = SettingsStore.Validate(s);
            if (problem != null) return problem;
            lock (s_settingsLock) {   // file and memory change together, one save at a time
                try { SettingsStore.Save(s); } catch (Exception e) { return "Could not save the settings: " + e.Message; }
                s_settings = s;
            }
            return null;
        }
        public void Saved() => TrayNative.PostMessageW(s_hwnd, WM_SETTINGS_SAVED, IntPtr.Zero, IntPtr.Zero);
    }

    // the options for this run: the settings file, then the tray app's own command line (e.g. --lan) on top
    private static Options CurrentOptions() { lock (s_settingsLock) return SettingsStore.ToOptions(s_settings.Clone(), s_args); }

    public static int Run(string[] args)
    {
        s_args = args;
        Options o;
        try {
            SettingsStore.OverridePath = Options.Parse(args).SettingsFile;
            s_settings = SettingsStore.Load();
            o = CurrentOptions();
        } catch (ArgumentException e) {
            TrayNative.MessageBoxW(IntPtr.Zero, e.Message, Title, TrayNative.MB_ICONWARNING);
            return 2;
        }
        using var mutex = new Mutex(true, MutexName(o.Instance), out bool first);
        if (!first) {   // already running: let that copy open its dashboard (it knows its port and runs as the right user);
                        // --no-browser (e.g. the start with Windows) only makes sure it runs
            // (only an explicit --no-browser: the "open on start" setting is about starting, not about a second double-click)
            if (!args.Contains("--no-browser") && EventWaitHandle.TryOpenExisting(OpenEventName(o.Instance), out var open)) using (open) open.Set();
            return 0;
        }
        try {
            try { s_host = new MonitorHost(o, s_backend); }
            catch (System.Net.Sockets.SocketException) {
                TrayNative.MessageBoxW(IntPtr.Zero, $"{Title} could not start: another program is already using port {o.Port}, " +
                    "which the dashboard needs.\n\nClose that program and start it again.", Title, TrayNative.MB_ICONWARNING);
                return 3;
            }
            using var openEvt = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEventName(o.Instance));
            using var exitEvt = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName(o.Instance));
            using var quitWatcher = new ManualResetEvent(false);

            if (!CreateWindow()) return 4;
            s_iconWatching = LoadIcon("tray-watching.ico");
            s_iconWaiting = LoadIcon("tray-waiting.ico");
            s_host.Start();
            AddIcon();
            TrayNative.SetTimer(s_hwnd, TimerId, 1000, IntPtr.Zero);

            // named events from other copies (open the dashboard / quit) arrive on this thread, then go to the window
            var watcher = new Thread(() => {
                var hs = new WaitHandle[] { openEvt, exitEvt, quitWatcher };
                try {
                    while (true) {
                        int i = WaitHandle.WaitAny(hs);
                        if (i == 2) return;
                        TrayNative.PostMessageW(s_hwnd, i == 0 ? WM_OPEN_REQUEST : WM_EXIT_REQUEST, IntPtr.Zero, IntPtr.Zero);
                        if (i == 1) return;
                    }
                } catch (ObjectDisposedException) { }   // shutting down
            }) { IsBackground = true, Name = "svsperf-tray-events" };
            watcher.Start();
            try {
                if (!o.NoBrowser) OpenUrl(s_host.Url);

                TrayNative.MSG msg;
                while (TrayNative.GetMessageW(&msg, IntPtr.Zero, 0, 0) > 0) {
                    TrayNative.TranslateMessage(&msg);
                    TrayNative.DispatchMessageW(&msg);
                }
            } finally {
                quitWatcher.Set();
                watcher.Join(2000);   // before its wait handles are disposed (end of this scope)
            }
            Shutdown();
            return 0;
        } finally {
            Shutdown();
            mutex.ReleaseMutex();
        }
    }

    // `SVSPerfMonitor --exit`: asks the running tray copy to quit cleanly, waits until it has. 0 = it quit, 1 = none ran.
    public static int SignalExit(string instance, int timeoutMs = 15000)
    {
        if (!EventWaitHandle.TryOpenExisting(ExitEventName(instance), out var evt)) return 1;
        using (evt) evt.Set();
        if (!Mutex.TryOpenExisting(MutexName(instance), out var m)) return 0;
        using (m) {
            try {
                if (!m.WaitOne(timeoutMs)) return 2;
                m.ReleaseMutex();
            } catch (AbandonedMutexException) { }   // it ended without releasing: gone all the same
        }
        return 0;
    }

    private static void Shutdown()
    {
        if (s_shutDown) return;
        s_shutDown = true;
        if (s_hwnd != IntPtr.Zero) TrayNative.KillTimer(s_hwnd, TimerId);
        RemoveIcon();
        s_host?.Dispose();   // stops the monitor and the frame capture (its ETW session)
        s_host = null;
        if (s_iconWatching != IntPtr.Zero) TrayNative.DestroyIcon(s_iconWatching);
        if (s_iconWaiting != IntPtr.Zero) TrayNative.DestroyIcon(s_iconWaiting);
        s_iconWatching = s_iconWaiting = IntPtr.Zero;
    }

    // ---- window ----

    private static bool CreateWindow()
    {
        IntPtr inst = TrayNative.GetModuleHandleW(null);
        fixed (char* cls = "SVSPerfMonitorTray") {
            var wc = new TrayNative.WNDCLASSEXW {
                cbSize = (uint)sizeof(TrayNative.WNDCLASSEXW),
                lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
                hInstance = inst,
                lpszClassName = cls,
            };
            if (TrayNative.RegisterClassExW(&wc) == 0) return false;
            // a hidden top-level window (not message-only): only those receive "TaskbarCreated" after Explorer restarts
            s_hwnd = TrayNative.CreateWindowExW(0, cls, cls, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        }
        fixed (char* tc = "TaskbarCreated") s_taskbarCreated = TrayNative.RegisterWindowMessageW(tc);
        return s_hwnd != IntPtr.Zero;
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try {
            switch (msg) {
                case WM_TRAY:
                    switch ((uint)(lParam.ToInt64() & 0xFFFF)) {
                        case TrayNative.WM_LBUTTONDBLCLK: case TrayNative.NIN_KEYSELECT: OpenUrl(DashboardUrl()); break;
                        case TrayNative.WM_CONTEXTMENU: ShowMenu(); break;
                    }
                    return IntPtr.Zero;
                case TrayNative.WM_TIMER:
                    if ((nuint)wParam == RestartTimerId) { TrayNative.KillTimer(hwnd, RestartTimerId); RestartMonitor(); }
                    else Refresh();
                    return IntPtr.Zero;
                case WM_SETTINGS_SAVED:
                    // a moment's delay: the page's request has been answered, its connection may still be closing
                    TrayNative.SetTimer(hwnd, RestartTimerId, 300, IntPtr.Zero);
                    return IntPtr.Zero;
                case WM_OPEN_REQUEST:
                    OpenUrl(DashboardUrl());
                    return IntPtr.Zero;
                case WM_EXIT_REQUEST:
                    TrayNative.DestroyWindow(hwnd);
                    return IntPtr.Zero;
                case TrayNative.WM_QUERYENDSESSION:
                    return 1;
                case TrayNative.WM_ENDSESSION:
                    if (wParam != IntPtr.Zero) Shutdown();   // logoff / shutdown: the process ends right after this
                    return IntPtr.Zero;
                case TrayNative.WM_DESTROY:
                    TrayNative.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
            if (msg == s_taskbarCreated && s_taskbarCreated != 0) {   // Explorer restarted: the icon is gone, add it again
                s_iconAdded = false;
                AddIcon();
                return IntPtr.Zero;
            }
        } catch { }   // nothing may escape into Windows' message dispatch
        return TrayNative.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static string DashboardUrl() => s_host?.Url ?? "http://127.0.0.1:8765/";

    // new settings: stop the monitor (its frame capture too) and start it again with them. A port another program uses:
    // back to the previous settings, and say so.
    private static void RestartMonitor()
    {
        var old = s_host;
        int oldPort = old != null ? new Uri(old.Url).Port : s_settings.Port;
        s_host = null;
        old?.Dispose();
        try {
            s_host = new MonitorHost(CurrentOptions(), s_backend);
        } catch (System.Net.Sockets.SocketException) {
            int wanted;
            lock (s_settingsLock) {
                wanted = s_settings.Port;
                var back = s_settings.Clone(); back.Port = oldPort;
                try { SettingsStore.Save(back); } catch { }
                s_settings = back;
            }
            TrayNative.MessageBoxW(IntPtr.Zero, $"Port {wanted} is already used by another program, so the dashboard stays on port {oldPort}.",
                Title, TrayNative.MB_ICONWARNING);
            try { s_host = new MonitorHost(CurrentOptions(), s_backend); } catch { }
        }
        s_host?.Start();
        Refresh();
    }

    private static void ShowMenu()
    {
        IntPtr menu = TrayNative.CreatePopupMenu();
        try {
            Append(menu, TrayNative.MF_STRING | TrayNative.MF_GRAYED, 100, s_status);
            Append(menu, TrayNative.MF_SEPARATOR, 0, null);
            Append(menu, TrayNative.MF_STRING, CmdOpen, "Open dashboard");
            Append(menu, TrayNative.MF_STRING, CmdLog, "Game log");
            Append(menu, TrayNative.MF_STRING, CmdSettings, "Settings");
            Append(menu, TrayNative.MF_SEPARATOR, 0, null);
            Append(menu, TrayNative.MF_STRING, CmdExit, "Exit");
            TrayNative.SetMenuDefaultItem(menu, CmdOpen, 0);
            TrayNative.POINT pt;
            TrayNative.GetCursorPos(&pt);
            TrayNative.SetForegroundWindow(s_hwnd);   // else the menu doesn't close when you click elsewhere
            int cmd = TrayNative.TrackPopupMenuEx(menu, TrayNative.TPM_RETURNCMD | TrayNative.TPM_RIGHTBUTTON | TrayNative.TPM_NONOTIFY,
                pt.x, pt.y, s_hwnd, IntPtr.Zero);
            TrayNative.PostMessageW(s_hwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
            switch (cmd) {
                case CmdOpen: OpenUrl(DashboardUrl()); break;
                case CmdLog: OpenUrl(DashboardUrl() + "log"); break;
                case CmdSettings: OpenUrl(DashboardUrl() + "settings"); break;
                case CmdExit: TrayNative.DestroyWindow(s_hwnd); break;
            }
        } finally {
            TrayNative.DestroyMenu(menu);
        }
    }

    private static void Append(IntPtr menu, uint flags, int id, string? text)
    {
        fixed (char* t = text) TrayNative.AppendMenuW(menu, flags, (nuint)id, t);
    }

    // ---- icon + tooltip ----

    // the plain-language status, as on the dashboard
    public static (bool watching, string status) Describe(Snapshot? s)
    {
        if (s == null || !s.Attached) return (false, "Waiting for the game");
        string what = s.Verdict.Label switch {
            "GPU-bound" => "Your graphics card is the limit",
            "CPU-bound" => "Your processor is the limit",
            "both near the limit" => "Your processor and graphics card are both near the limit",
            "neither" => "Neither is the limit right now",
            "mixed" => "The limit is changing",
            _ => "Watching the game",
        };
        var f = s.Frames;
        if (f != null && f.State == "running" && f.Frames > 0 && f.Fps > 0) what += $" · {f.Fps:0} FPS";
        return (true, what);
    }

    private static void Refresh()
    {
        var (watching, status) = Describe(s_host?.Latest);
        string tip = $"{Title}\n{status}";
        if (tip.Length > 127) tip = tip[..127];
        if (watching == s_watching && tip == s_tip && s_iconAdded) return;
        s_watching = watching; s_tip = tip; s_status = status;
        if (!s_iconAdded) AddIcon(); else Notify(TrayNative.NIM_MODIFY);
    }

    private static void AddIcon()
    {
        if (s_tip.Length == 0) s_tip = $"{Title}\n{s_status}";
        if (Notify(TrayNative.NIM_ADD)) {
            s_iconAdded = true;
            var d = Data();
            d.uVersion = TrayNative.NOTIFYICON_VERSION_4;
            TrayNative.Shell_NotifyIconW(TrayNative.NIM_SETVERSION, &d);
        }
    }

    private static void RemoveIcon()
    {
        if (!s_iconAdded) return;
        var d = Data();
        TrayNative.Shell_NotifyIconW(TrayNative.NIM_DELETE, &d);
        s_iconAdded = false;
    }

    private static TrayNative.NOTIFYICONDATAW Data()
    {
        var d = new TrayNative.NOTIFYICONDATAW { cbSize = (uint)sizeof(TrayNative.NOTIFYICONDATAW), hWnd = s_hwnd, uID = 1 };
        return d;
    }

    private static bool Notify(uint op)
    {
        var d = Data();
        d.uFlags = TrayNative.NIF_MESSAGE | TrayNative.NIF_ICON | TrayNative.NIF_TIP | TrayNative.NIF_SHOWTIP;
        d.uCallbackMessage = WM_TRAY;
        d.hIcon = s_watching ? s_iconWatching : s_iconWaiting;
        int n = Math.Min(s_tip.Length, 127);
        for (int i = 0; i < n; i++) d.szTip[i] = s_tip[i];
        d.szTip[n] = '\0';
        return TrayNative.Shell_NotifyIconW(op, &d);
    }

    // an embedded .ico (PNG entries): the entry closest to the small-icon size for this DPI, as an HICON
    private static IntPtr LoadIcon(string resource)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (s == null) return IntPtr.Zero;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        byte[] ico = ms.ToArray();
        int want = TrayNative.GetSystemMetrics(TrayNative.SM_CXSMICON);
        int count = BitConverter.ToUInt16(ico, 4), best = -1, bestSize = 0;
        for (int i = 0; i < count; i++) {
            int size = ico[6 + 16 * i]; if (size == 0) size = 256;
            bool better = best < 0 || (size >= want && (bestSize < want || size < bestSize)) || (bestSize < want && size > bestSize);
            if (better) { best = i; bestSize = size; }
        }
        if (best < 0) return IntPtr.Zero;
        int len = BitConverter.ToInt32(ico, 6 + 16 * best + 8), off = BitConverter.ToInt32(ico, 6 + 16 * best + 12);
        fixed (byte* p = &ico[off])
            return TrayNative.CreateIconFromResourceEx(p, (uint)len, true, 0x00030000, want, want, 0);
    }

    // ---- browser ----

    // opens a URL in the user's normal browser. Elevated, a browser we start would run as admin too: then explorer.exe
    // hands the URL to the already running (normal) shell instead.
    public static void OpenUrl(string url)
    {
        try {
            if (FrameMonitor.IsElevated()) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + url + "\"") { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        } catch { }
    }
}

internal static unsafe class TrayNative
{
    public const uint WM_APP = 0x8000, WM_DESTROY = 0x0002, WM_TIMER = 0x0113, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016;
    public const uint WM_CONTEXTMENU = 0x007B, WM_LBUTTONDBLCLK = 0x0203, NIN_KEYSELECT = 0x0401;
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4, NOTIFYICON_VERSION_4 = 4;
    public const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    public const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_SEPARATOR = 0x800;
    public const uint TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;
    public const uint MB_ICONWARNING = 0x30;
    public const int SM_CXSMICON = 49;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;   // union with uTimeout
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("user32.dll")] public static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport("user32.dll")] public static extern IntPtr CreateWindowExW(uint exStyle, char* cls, char* name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int GetMessageW(MSG* msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(MSG* msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(MSG* msg);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint RegisterWindowMessageW(char* name);
    [DllImport("user32.dll")] public static extern nuint SetTimer(IntPtr hwnd, nuint id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr hwnd, nuint id);
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] public static extern bool AppendMenuW(IntPtr menu, uint flags, nuint id, char* text);
    [DllImport("user32.dll")] public static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPos);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(POINT* pt);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern IntPtr CreateIconFromResourceEx(byte* bits, uint size, bool icon, uint ver, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
    [DllImport("shell32.dll")] public static extern bool Shell_NotifyIconW(uint op, NOTIFYICONDATAW* data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public uint cb; public IntPtr lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? app, char* cmdLine, IntPtr procAttr, IntPtr threadAttr, bool inherit,
        uint flags, IntPtr env, string? dir, STARTUPINFOW* si, PROCESS_INFORMATION* pi);

    // starts a console program with no console window at all (CREATE_NO_WINDOW works on every Windows version) and no
    // inherited handles
    public static bool StartDetached(string exe, string[] args)
    {
        var sb = new System.Text.StringBuilder(Quote(exe));
        foreach (var a in args) sb.Append(' ').Append(Quote(a));
        string cmd = sb.ToString();
        var si = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW) };
        PROCESS_INFORMATION pi;
        fixed (char* c = cmd + "\0") {   // CreateProcessW may write into the command line buffer: pass a private copy
            if (!CreateProcessW(exe, c, IntPtr.Zero, IntPtr.Zero, false, 0x08000000 /* CREATE_NO_WINDOW */, IntPtr.Zero, null, &si, &pi))
                return false;
        }
        Native.CloseHandle(pi.hThread);
        Native.CloseHandle(pi.hProcess);
        return true;
    }

    // one argument for CommandLineToArgvW / the C runtime: quoted when needed, backslashes before quotes doubled
    private static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', '"']) < 0) return a;
        var sb = new System.Text.StringBuilder("\"");
        int bs = 0;
        foreach (char ch in a) {
            if (ch == '\\') { bs++; continue; }
            if (ch == '"') { sb.Append('\\', bs * 2 + 1).Append('"'); bs = 0; continue; }
            sb.Append('\\', bs).Append(ch); bs = 0;
        }
        return sb.Append('\\', bs * 2).Append('"').ToString();
    }
}
