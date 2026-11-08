// AutoStart.cs - the Windows side of three settings (#7):
//   Start with Windows       HKCU\...\Run entry (no admin) -> the starter with --tray --autostart
//   Run with full permissions a Task Scheduler task "run with highest privileges" for this user (one admin prompt to create
//                            or remove); with "start with Windows" it has a logon trigger. A normal start then hands over
//                            to it (schtasks /Run: elevated, no prompt).
//   Frame times setup        this user into BUILTIN\Performance Log Users (S-1-5-32-559; one admin prompt, then sign out)
// The admin steps run in a helper mode of this same program, started elevated ("runas"). The helper takes no paths or
// names from its caller: the task always runs the starter next to the helper, the user is a SID it validates.
// Copyright (c) 2026 Jared Brown. MIT licence.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace FSPerf;

public static unsafe class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string PerfLogUsersSid = "S-1-5-32-559";

    public static string TaskName(string instance) => instance == "SVSPerfMonitor" ? "SVS Performance Monitor" : instance;

    // what players start: the starter next to the program (published), or the program itself (a development build)
    public static string StarterPath()
    {
        string self = Environment.ProcessPath!;
        string starter = Path.ChangeExtension(self, ".exe");
        return self.EndsWith(".com", StringComparison.OrdinalIgnoreCase) && File.Exists(starter) ? starter : self;
    }

    // ---- start with Windows (no admin) ----

    public static bool RunEntryExists(string instance)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(instance) is string;
    }

    public static void SetRunEntry(string instance, bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(instance, $"\"{StarterPath()}\" --tray --autostart" + (instance != "SVSPerfMonitor" ? $" --instance {instance}" : ""));
        else if (k.GetValue(instance) != null) k.DeleteValue(instance, false);
    }

    // ---- full permissions: the scheduled task (admin) ----

    public static bool IsElevated() => FrameMonitor.IsElevated();

    // the task's definition: this user, highest privileges, the starter next to this program; logon trigger on request
    public static string TaskXml(string userSid, string starter, bool atLogon, string instance)
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s);
        string args = "--tray --autostart --handover" + (instance != "SVSPerfMonitor" ? $" --instance {instance}" : "");
        string trigger = atLogon ? $"<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{X(userSid)}</UserId></LogonTrigger></Triggers>" : "<Triggers />";
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
            "<Task version=\"1.3\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
            "<RegistrationInfo><Author>SimVizion Studios</Author><Description>SVS Performance Monitor with full permissions " +
            "(created from its Settings page; turn \"Run with full permissions\" off there to remove it).</Description></RegistrationInfo>" +
            trigger +
            $"<Principals><Principal id=\"Author\"><UserId>{X(userSid)}</UserId><LogonType>InteractiveToken</LogonType>" +
            "<RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
            "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate>" +
            "<StartWhenAvailable>false</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>" +
            "<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
            "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden>" +
            "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>" +
            $"<Actions Context=\"Author\"><Exec><Command>{X(starter)}</Command><Arguments>{X(args)}</Arguments></Exec></Actions></Task>";
    }

    public static bool TaskExists(string instance) => Schtasks($"/Query /TN \"{TaskName(instance)}\"") == 0;

    // starts the full-permissions copy without a prompt (the task runs elevated)
    public static bool RunTask(string instance) => Schtasks($"/Run /TN \"{TaskName(instance)}\"") == 0;

    private static int Schtasks(string args)
    {
        try {
            var psi = new ProcessStartInfo("schtasks.exe", args) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            return p.WaitForExit(15000) ? p.ExitCode : -1;
        } catch { return -1; }
    }

    // ---- frame times: Performance Log Users ----

    // "ready" (this login can read frame events), "signout" (the account is in the group, this login predates it),
    // "setup" (not in the group, not elevated)
    public static string FrameTimesAccess()
    {
        using var id = WindowsIdentity.GetCurrent();
        if (IsElevated() || new WindowsPrincipal(id).IsInRole(new SecurityIdentifier(PerfLogUsersSid))) return "ready";
        return AccountInPerfLogUsers(id.User!.Value) ? "signout" : "setup";
    }

    private static bool AccountInPerfLogUsers(string userSid)
    {
        string group = GroupName();
        IntPtr buf = IntPtr.Zero;
        try {
            nint resume = 0;
            if (NetLocalGroupGetMembers(null, group, 0, out buf, -1, out int read, out _, ref resume) != 0) return false;
            for (int i = 0; i < read; i++) {
                IntPtr sid = Marshal.ReadIntPtr(buf, i * IntPtr.Size);
                if (new SecurityIdentifier(sid).Value == userSid) return true;
            }
        } catch { } finally { if (buf != IntPtr.Zero) NetApiBufferFree(buf); }
        return false;
    }

    // the group's name in this Windows' language
    private static string GroupName() =>
        ((NTAccount)new SecurityIdentifier(PerfLogUsersSid).Translate(typeof(NTAccount))).Value.Split('\\')[^1];

    // ---- the elevated helper ----

    public const int ExitOk = 0, ExitCancelled = 1223, ExitFailed = 1;

    // runs this program elevated with `args` (a Windows prompt) and waits. ExitCancelled when the prompt was refused.
    public static int RunElevated(string args)
    {
        var sei = new SHELLEXECUTEINFOW { cbSize = (uint)sizeof(SHELLEXECUTEINFOW), fMask = 0x40 /* SEE_MASK_NOCLOSEPROCESS */, nShow = 0 };
        string file = StarterPath();   // published: the GUI starter, so no console window flashes (Windows 10 too)
        fixed (char* verb = "runas") fixed (char* f = file) fixed (char* a = args) {
            sei.lpVerb = verb; sei.lpFile = f; sei.lpParameters = a;
            if (!ShellExecuteExW(&sei)) return Marshal.GetLastWin32Error() == 1223 ? ExitCancelled : ExitFailed;
        }
        try {
            WaitForSingleObject(sei.hProcess, 60000);
            return GetExitCodeProcess(sei.hProcess, out uint code) ? (int)code : ExitFailed;
        } finally { Native.CloseHandle(sei.hProcess); }
    }

    // `--admin task-set <sid> <logon 0|1> <instance>` / `--admin task-delete <instance>` / `--admin perflog <sid>`
    public static int AdminMain(string[] a)
    {
        try {
            if (!IsElevated()) return ExitFailed;
            switch (a.ElementAtOrDefault(1)) {
                case "task-set": {
                    string sid = ValidUserSid(a[2]);
                    string instance = ValidInstance(a[4]);
                    string xml = TaskXml(sid, StarterPath(), a[3] == "1", instance);
                    string tmp = Path.Combine(Path.GetTempPath(), $"svsperf_task_{Guid.NewGuid():N}.xml");
                    try {
                        File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
                        return Schtasks($"/Create /TN \"{TaskName(instance)}\" /XML \"{tmp}\" /F") == 0 ? ExitOk : ExitFailed;
                    } finally { try { File.Delete(tmp); } catch { } }
                }
                case "task-delete": {
                    string instance = ValidInstance(a[2]);
                    int r = Schtasks($"/Delete /TN \"{TaskName(instance)}\" /F");
                    return r == 0 || Schtasks($"/Query /TN \"{TaskName(instance)}\"") != 0 ? ExitOk : ExitFailed;
                }
                case "perflog": {
                    var sid = new SecurityIdentifier(ValidUserSid(a[2]));
                    byte[] bin = new byte[sid.BinaryLength];
                    sid.GetBinaryForm(bin, 0);
                    fixed (byte* p = bin) {
                        IntPtr member = (IntPtr)p;
                        uint r = NetLocalGroupAddMembers(null, GroupName(), 0, &member, 1);
                        return r == 0 || r == 1378 /* already a member */ ? ExitOk : ExitFailed;
                    }
                }
            }
        } catch { }
        return ExitFailed;
    }

    // a real user account's SID (S-1-5-21-...): never a group, service or well-known SID
    private static string ValidUserSid(string s)
    {
        var sid = new SecurityIdentifier(s);
        if (!sid.Value.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !sid.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal))
            throw new ArgumentException("not a user SID");
        return sid.Value;
    }

    private static string ValidInstance(string s) =>
        s.Length is > 0 and <= 64 && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? s : throw new ArgumentException("instance");

    public static string CurrentUserSid() { using var id = WindowsIdentity.GetCurrent(); return id.User!.Value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHELLEXECUTEINFOW
    {
        public uint cbSize, fMask; public IntPtr hwnd; public char* lpVerb, lpFile, lpParameters, lpDirectory; public int nShow;
        public IntPtr hInstApp, lpIDList, lpClass, hkeyClass; public uint dwHotKey; public IntPtr hIconOrMonitor, hProcess;
    }
    [DllImport("shell32.dll", SetLastError = true)] private static extern bool ShellExecuteExW(SHELLEXECUTEINFOW* info);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint NetLocalGroupAddMembers(string? server, string group, uint level, IntPtr* members, uint count);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint NetLocalGroupGetMembers(string? server, string group, uint level, out IntPtr buf, int prefMax,
        out int read, out int total, ref nint resume);
    [DllImport("netapi32.dll")] private static extern uint NetApiBufferFree(IntPtr buf);
}
