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

    // per Windows user (the task's principal and its protected copy belong to one account; another user's must never be
    // overwritten or removed): the name carries the user's SID
    public static string TaskName(string instance, string userSid) =>
        (instance == "SVSPerfMonitor" ? "SVS Performance Monitor" : instance) + " - " + userSid;

    // what players start: the starter next to the program (published), or the program itself (a development build)
    public static string StarterPath()
    {
        string self = Environment.ProcessPath!;
        string starter = Path.ChangeExtension(self, ".exe");
        return IsPublished() ? starter : self;
    }

    // the published pair (SVSPerfMonitor.com + its starter .exe), not a development build (which needs its .dll next to it)
    public static bool IsPublished()
    {
        string self = Environment.ProcessPath!;
        return self.EndsWith(".com", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(self, ".exe"));
    }

    // ---- the protected copy (full permissions) ----
    // A task that runs elevated without a prompt must never run files the user (or anything running as the user) can
    // change: that would be a way around UAC (replace the program or PresentMon, then trigger the task). So the task runs
    // a copy in Program Files, which only administrators can write, made by the elevated helper.

    public static string ProtectedDir(string instance, string userSid) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SimVizion Studios",
        instance == "SVSPerfMonitor" ? "SVS Performance Monitor" : instance, userSid);

    // the task can start (its protected starter is there): checked before handing over to it
    public static bool ProtectedCopyReady(string instance) =>
        File.Exists(Path.Combine(ProtectedDir(instance, CurrentUserSid()), "SVSPerfMonitor.exe")) && TaskExists(instance);

    public static bool RunsFromProtectedDir()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\') + "\\";
        return Environment.ProcessPath!.StartsWith(pf, StringComparison.OrdinalIgnoreCase);
    }

    // system tools by full path: an elevated process must never pick up a same-named file planted next to it
    private static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    // ---- start with Windows (no admin) ----

    public static bool RunEntryExists(string instance)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(instance) is string;
    }

    // `starter` = the user's own copy (an elevated copy runs from Program Files, which goes when full permissions go)
    public static void SetRunEntry(string instance, bool on, string? starter = null)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(instance, $"\"{starter ?? StarterPath()}\" --tray --autostart" + (instance != "SVSPerfMonitor" ? $" --instance {instance}" : ""));
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

    public static bool TaskExists(string instance) => Schtasks($"/Query /TN \"{TaskName(instance, CurrentUserSid())}\"") == 0;

    // starts the full-permissions copy without a prompt (the task runs elevated)
    public static bool RunTask(string instance) => Schtasks($"/Run /TN \"{TaskName(instance, CurrentUserSid())}\"") == 0;

    private static int Schtasks(string args)
    {
        try {
            var psi = new ProcessStartInfo(System32("schtasks.exe"), args) {
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

    public const int ExitOk = 0, ExitCancelled = 1223, ExitFailed = 1, ExitNotPublished = 2, ExitFilesInUse = 3;

    // runs this program elevated with `args` (a Windows prompt) and waits. ExitCancelled when the prompt was refused.
    // An admin step: an elevated copy (the protected one) does it in-process - it must never start another program, since
    // that would get no prompt and nothing outside Program Files may run elevated. A normal copy starts its own helper
    // with a Windows permission prompt (the user sees and approves exactly this program).
    public static int Admin(string args)
    {
        if (IsElevated()) return AdminMain(["--admin", .. args.Split(' ', StringSplitOptions.RemoveEmptyEntries)]);
        return RunElevated("--admin " + args);
    }

    private static int RunElevated(string args)
    {
        var sei = new SHELLEXECUTEINFOW { cbSize = (uint)sizeof(SHELLEXECUTEINFOW), fMask = 0x40 /* SEE_MASK_NOCLOSEPROCESS */, nShow = 0 };
        string file = StarterPath();   // this copy's own starter: no console window flashes (Windows 10 too)
        fixed (char* verb = "runas") fixed (char* f = file) fixed (char* a = args) {
            sei.lpVerb = verb; sei.lpFile = f; sei.lpParameters = a;
            if (!ShellExecuteExW(&sei)) return Marshal.GetLastWin32Error() == 1223 ? ExitCancelled : ExitFailed;
        }
        try {
            WaitForSingleObject(sei.hProcess, 60000);
            return GetExitCodeProcess(sei.hProcess, out uint code) ? (int)code : ExitFailed;
        } finally { Native.CloseHandle(sei.hProcess); }
    }

    // `--admin task-set <sid> <logon 0|1> <instance>` / `--admin task-delete <sid> <instance>` / `--admin perflog <sid>`
    public static int AdminMain(string[] a)
    {
        try {
            if (!IsElevated()) return ExitFailed;
            switch (a.ElementAtOrDefault(1)) {
                case "task-set": {
                    string sid = ValidUserSid(a[2]);
                    string instance = ValidInstance(a[4]);
                    if (!IsPublished()) return ExitNotPublished;
                    // 1. the protected copy: this program, its starter and PresentMon, from the helper's own folder
                    string dir = ProtectedDir(instance, sid);
                    Directory.CreateDirectory(dir);   // inherits Program Files' ACL: administrators write, users read
                    string from = Path.GetDirectoryName(Environment.ProcessPath)!;
                    var files = new List<string> { Path.GetFileName(Environment.ProcessPath)!, Path.GetFileName(StarterPath()) };
                    files.AddRange(Directory.GetFiles(from, "PresentMon*.exe").Select(Path.GetFileName)!);
                    foreach (var f in files) {
                        string src = Path.Combine(from, f), dst = Path.Combine(dir, f);
                        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) continue;
                        if (SameFile(src, dst)) continue;
                        try { File.Copy(src, dst, overwrite: true); } catch (IOException) { return ExitFilesInUse; }
                    }
                    // 2. the task, for the protected starter. Its definition goes through the protected folder too, never
                    // through the user's %TEMP%, where it could be swapped between writing and reading.
                    string xml = TaskXml(sid, Path.Combine(dir, Path.GetFileName(StarterPath())), a[3] == "1", instance);
                    string xmlFile = Path.Combine(dir, $"task_{Guid.NewGuid():N}.xml");
                    try {
                        File.WriteAllText(xmlFile, xml, System.Text.Encoding.Unicode);
                        return Schtasks($"/Create /TN \"{TaskName(instance, sid)}\" /XML \"{xmlFile}\" /F") == 0 ? ExitOk : ExitFailed;
                    } finally { try { File.Delete(xmlFile); } catch { } }
                }
                case "task-delete": {
                    string sid = ValidUserSid(a[2]);
                    string instance = ValidInstance(a[3]);
                    int r = Schtasks($"/Delete /TN \"{TaskName(instance, sid)}\" /F");
                    bool gone = r == 0 || Schtasks($"/Query /TN \"{TaskName(instance, sid)}\"") != 0;
                    // the protected copy goes too. Files still in use (the full-permissions copy still runs, and its
                    // PresentMon) are removed at the next restart instead (MoveFileEx delay-until-reboot), then their folders.
                    try {
                        string dir = ProtectedDir(instance, sid);
                        if (Directory.Exists(dir)) {
                            bool later = false;
                            foreach (var f in Directory.GetFiles(dir)) {
                                try { File.Delete(f); }
                                catch { later = true; MoveFileExW(f, null, 4 /* MOVEFILE_DELAY_UNTIL_REBOOT */); }
                            }
                            var dirs = new List<string> { dir };
                            for (string? p = Path.GetDirectoryName(dir); p != null && p.Contains("SimVizion Studios"); p = Path.GetDirectoryName(p)) dirs.Add(p);
                            foreach (var d in dirs) {   // innermost first; never a folder that still holds another user's copy
                                if (!Directory.Exists(d)) continue;
                                bool empty = !Directory.EnumerateFileSystemEntries(d).Any();
                                if (empty) { try { Directory.Delete(d); continue; } catch { } }
                                if (later && (d == dir || Directory.EnumerateFileSystemEntries(d).All(e => string.Equals(e, dirs[dirs.IndexOf(d) - 1], StringComparison.OrdinalIgnoreCase))))
                                    MoveFileExW(d, null, 4);   // after its files, at the restart (empty by then)
                                else break;
                            }
                        }
                    } catch { }
                    return gone ? ExitOk : ExitFailed;
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

    private static bool SameFile(string a, string b)
    {
        if (!File.Exists(b) || new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var sa = File.OpenRead(a); using var sb = File.OpenRead(b);
        return System.Security.Cryptography.SHA256.HashData(sa).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(sb));
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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool MoveFileExW(string from, string? to, uint flags);
}
