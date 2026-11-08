// EtwSessions.cs - housekeeping for the monitor's own ETW trace sessions (SVSPerf_<pid>). PresentMon opens one per capture; a
// killed monitor (Stop-Process -Force, crash, power loss) never stops it, and an orphaned session starves every later
// PresentMon of events ("frames stuck on starting", issue #9). Stopping a session needs the same rights as PresentMon
// (admin or Performance Log Users): every failure here is non-fatal and logged.
// Copyright (c) 2026 Jared Brown. MIT licence.
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FSPerf;

public static class EtwSessions
{
    public const string Prefix = "SVSPerf_";
    private static readonly Regex NameRx = new(@"^(" + Prefix + @"(\d+))(?:\s|$)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static void Log(string msg) => Console.Error.WriteLine("SVSPerfMonitor: " + msg);

    // the SVSPerf_<pid> session names in `logman query -ets` output, with their pids
    public static List<(string Name, int Pid)> Parse(string logmanOutput)
    {
        var r = new List<(string, int)>();
        foreach (Match m in NameRx.Matches((logmanOutput ?? "").TrimStart('﻿'))  /* a piped test input may carry a BOM */)
            if (int.TryParse(m.Groups[2].Value, out int pid)) r.Add((m.Groups[1].Value, pid));
        return r;
    }

    // pure decision: the sessions to stop = those whose pid is not a live FSPerf process (and never our own)
    public static List<string> Stale(IEnumerable<(string Name, int Pid)> sessions, int selfPid, Func<int, bool> isLiveFsperf)
        => sessions.Where(s => s.Pid != selfPid && !isLiveFsperf(s.Pid)).Select(s => s.Name).ToList();

    // a pid is a live FSPerf when a process with it exists and runs the same program name as we do (guards pid reuse)
    public static bool IsLiveFsperf(int pid)
    {
        try {
            using var p = Process.GetProcessById(pid);
            using var me = Process.GetCurrentProcess();
            return !p.HasExited && IsFsperfProcessName(p.ProcessName, me.ProcessName);
        } catch { return false; }  // no such process
    }

    // conservative: SVSPerfMonitor.exe, our own program name, or dotnet (`dotnet SVSPerfMonitor.dll`) all count as live -
    // a session is stopped only when its pid is gone or clearly some other program
    public static bool IsFsperfProcessName(string name, string ownName)
        => name.Equals(ownName, StringComparison.OrdinalIgnoreCase) || name.Equals("SVSPerfMonitor", StringComparison.OrdinalIgnoreCase)
           || name.Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    private static (int Code, string Out) Logman(string args)
    {
        var psi = new ProcessStartInfo("logman.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return (-1, "logman timed out"); }
        return (p.ExitCode, (o.Result + " " + e.Result).Trim());
    }

    // stop one session; false (and a log line) when logman cannot, e.g. no rights. A session that is not there is success.
    public static bool Stop(string name)
    {
        try {
            var (code, text) = Logman($"stop {name} -ets");
            if (code == 0) return true;
            if (text.Contains("not found", StringComparison.OrdinalIgnoreCase) || text.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                || code == unchecked((int)0x80300104)) return true;  // already gone
            Log($"could not stop ETW session {name} (logman exit {code}: {text}); needs admin or Performance Log Users");
            return false;
        } catch (Exception e) {
            Log($"could not stop ETW session {name}: {e.Message}");
            return false;
        }
    }

    // startup: stop every FSPerf_* session left behind by a dead fsperf
    public static void CleanupStale()
    {
        try {
            var (code, text) = Logman("query -ets");
            if (code != 0) { Log($"could not list ETW sessions (logman exit {code}); stale-session cleanup skipped"); return; }
            using var me = Process.GetCurrentProcess();
            foreach (var name in Stale(Parse(text), me.Id, IsLiveFsperf))
                if (Stop(name)) Log($"stopped stale ETW session {name} (its monitor process is gone)");
        } catch (Exception e) {
            Log("stale ETW session cleanup failed: " + e.Message);
        }
    }
}
