// FrameMonitor.cs - full mode: per-frame CPU / GPU timing of the game from Windows' presentation events, read through
// Intel's PresentMon console (MIT; https://github.com/GameTechDev/PresentMon) as a CSV stream. It needs admin rights or
// the "Performance Log Users" group (an ETW trace session). FSPerf looks for PresentMon*.exe next to itself, in ..\tools\,
// or where --presentmon points. Columns are matched by name (2.x metrics; 1.x names as a fallback), missing ones = NaN.
// Copyright (c) 2026 Jared Brown. MIT licence.
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;

namespace FSPerf;

public sealed record FrameStats(
    string State, string Hint, int Frames, double Fps, double FrameTimeAvgMs, double FrameTimeP50Ms, double FrameTimeP99Ms,
    double Low1PctFps, double CpuBusyMs, double CpuWaitMs, double GpuBusyMs, double GpuWaitMs, double GpuLatencyMs,
    double DisplayLatencyMs, string PresentMode, string Runtime);

public sealed class FrameMonitor : IDisposable
{
    // per FSPerf process: --stop_existing_session / --terminate_existing_session must never hit another instance's capture
    private static readonly string Session = EtwSessions.Prefix + Environment.ProcessId;
    private readonly string? _exe;
    private Process? _pm;
    private int _pid;
    private readonly object _lock = new();
    private readonly List<double[]> _frames = new();  // FrameTime, CPUBusy, CPUWait, GPUBusy, GPUWait, GPULatency, DisplayLatency
    private string _presentMode = "", _runtime = "";
    private string _state = "off", _hint = "";
    private readonly List<string> _stderr = new();
    private DateTime _lastStart = DateTime.MinValue;
    // the capture generation: readers of a stopped capture must not write into the next one's state
    private int _gen;
    private readonly List<Thread> _readers = new();

    public FrameMonitor(string? explicitPath)
    {
        _exe = Locate(explicitPath);
        if (_exe == null) {
            _state = "off";
            _hint = "PresentMon not found: put PresentMon-<version>-x64.exe next to SVSPerfMonitor.exe or in tools\\ (build.ps1 -FetchPresentMon), or pass --presentmon <path>";
            return;
        }
        // a killed fsperf never stops its ETW session, and an orphan starves PresentMon of events (issue #9)
        EtwSessions.CleanupStale();
        // every normal exit path (Ctrl+C, console close, unhandled exception) stops our session; a kill cannot, the next start cleans up
        AppDomain.CurrentDomain.ProcessExit += (_, _) => EtwSessions.Stop(Session);
        AppDomain.CurrentDomain.UnhandledException += (_, _) => EtwSessions.Stop(Session);
        // closing the console window / logoff / shutdown ends the process without ProcessExit; Windows allows ~5 s here.
        // Ctrl+C / Ctrl+Break are left to Console.CancelKeyPress (graceful exit through Dispose).
        try {
            _ctrlHandler = ct => {
                if (ct == ConsoleCtrl.CTRL_CLOSE_EVENT || ct == ConsoleCtrl.CTRL_LOGOFF_EVENT || ct == ConsoleCtrl.CTRL_SHUTDOWN_EVENT)
                    EtwSessions.Stop(Session);
                return false;  // the default handler still ends the process
            };
            ConsoleCtrl.SetConsoleCtrlHandler(_ctrlHandler, true);
        } catch { }
    }

    private static ConsoleCtrl.Handler? _ctrlHandler;  // static: the delegate must outlive the native registration

    // header watchdog: PresentMon is running but has printed no CSV header (no events reach it, e.g. a starved or half-dead
    // ETW session) for this long -> restart the capture once per game process, and say why
    private const double WatchdogSeconds = 20;
    private DateTime _captureStart = DateTime.MinValue;
    private int _watchdogPid;      // the game pid the one allowed restart was spent on

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool InPerformanceLogUsers()
    {
        using var id = WindowsIdentity.GetCurrent();
        // S-1-5-32-559 = BUILTIN\Performance Log Users
        return id.Groups?.Any(g => g.Value == "S-1-5-32-559") ?? false;
    }

    public static bool PresentMonAvailable(string? explicitPath) => Locate(explicitPath) != null;

    private static string? Locate(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath)) return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        string baseDir = AppContext.BaseDirectory;
        var dirs = new List<string> { baseDir, Path.Combine(baseDir, "tools") };
        // a build tree: <repo>\src\bin\Release\net10.0-windows\win-x64\ -> <repo>\tools\
        for (var d = new DirectoryInfo(baseDir); d != null; d = d.Parent) {
            if (File.Exists(Path.Combine(d.FullName, "build.ps1")) && Directory.Exists(Path.Combine(d.FullName, "tools"))) {
                dirs.Add(Path.Combine(d.FullName, "tools"));
                break;
            }
        }
        foreach (var dir in dirs) {
            if (!Directory.Exists(dir)) continue;
            var hit = Directory.GetFiles(dir, "PresentMon*.exe").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (hit != null) return hit;
        }
        return null;
    }

    // (re)start the capture for this process; called by the collector each tick while the game runs
    public void Ensure(int pid)
    {
        if (_exe == null) return;
        bool restart = false;
        if (_pm != null && !_pm.HasExited && _pid == pid) {
            string st; string lastErr = "";
            lock (_lock) { st = _state; if (_stderr.Count > 0) lastErr = _stderr[^1]; }
            double waited = (DateTime.UtcNow - _captureStart).TotalSeconds;
            if (st != "starting" || waited < WatchdogSeconds) return;
            if (_watchdogPid == pid) {   // the one restart did not help: keep waiting, but tell the user what PresentMon says
                lock (_lock) if (_state == "starting")
                    _hint = $"no frames from PresentMon after {waited:0} s and a restart{(lastErr.Length > 0 ? " (PresentMon: " + lastErr + ")" : "")}; if an old SVSPerf_* ETW session is stuck run: logman stop SVSPerf_<pid> -ets (admin)";
                return;
            }
            _watchdogPid = pid;
            restart = true;
            EtwSessions.Log($"no PresentMon frames {waited:0} s after start{(lastErr.Length > 0 ? " (" + lastErr + ")" : "")}; restarting the capture and its ETW session once");
        }
        if (_state == "denied" && _pid == pid) return;               // no point retrying until FSPerf restarts elevated
        if (!restart && (DateTime.UtcNow - _lastStart).TotalSeconds < 10) return; // a crashing capture: back off
        if (_pid != pid) _watchdogPid = 0;  // a new game process gets its own one restart
        StopCapture();
        if (restart) EtwSessions.Stop(Session);  // a wedged session is what we are working around: start from a clean one
        _captureStart = DateTime.UtcNow;
        _pid = pid;
        _lastStart = DateTime.UtcNow;
        lock (_lock) { _frames.Clear(); _stderr.Clear(); }
        var psi = new ProcessStartInfo(_exe,
            $"--process_id {pid} --output_stdout --no_console_stats --v2_metrics --session_name {Session} --stop_existing_session --terminate_on_proc_exit") {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        try {
            _pm = Process.Start(psi);
        } catch (Exception e) {
            _state = "error";
            _hint = "PresentMon did not start: " + e.Message;
            return;
        }
        if (_pm == null) return;
        _state = "starting";
        _hint = "";
        var pm = _pm;
        int gen;
        lock (_lock) gen = ++_gen;
        // the readers are taken once, here: Process.StandardOutput throws once the Process is disposed
        var outR = pm.StandardOutput;
        var errR = pm.StandardError;
        var tOut = new Thread(() => ReadCsv(pm, outR, gen)) { IsBackground = true, Name = "fsperf-presentmon" };
        var tErr = new Thread(() => {
            try {
                string? l;
                while ((l = errR.ReadLine()) != null) lock (_lock) { if (gen == _gen && _stderr.Count < 50) _stderr.Add(l); }
            } catch { }  // the capture was stopped under us
        }) { IsBackground = true, Name = "fsperf-presentmon-err" };
        _readers.Clear();
        _readers.Add(tOut);
        _readers.Add(tErr);
        tOut.Start();
        tErr.Start();
    }

    private void ReadCsv(Process pm, StreamReader outR, int gen)
    {
        try { ReadCsvCore(pm, outR, gen); }
        catch { }  // the capture was stopped under us: an old generation has nothing to report
    }

    private void ReadCsvCore(Process pm, StreamReader outR, int gen)
    {
        string? header = outR.ReadLine();
        if (header == null) { Finished(pm, gen); return; }
        var cols = header.Split(',').Select(c => c.Trim()).ToList();
        int Col(params string[] names) { foreach (var n in names) { int i = cols.FindIndex(c => c.Equals(n, StringComparison.OrdinalIgnoreCase)); if (i >= 0) return i; } return -1; }
        int[] idx = {
            Col("FrameTime", "MsBetweenPresents"), Col("CPUBusy"), Col("CPUWait"), Col("GPUBusy", "MsGPUActive"), Col("GPUWait"),
            Col("GPULatency"), Col("DisplayLatency", "MsUntilDisplayed"),
        };
        int mode = Col("PresentMode"), runtime = Col("PresentRuntime", "Runtime");
        lock (_lock) { if (gen != _gen) return; _state = "running"; _hint = idx[0] < 0 ? "PresentMon CSV has no FrameTime column: " + header : ""; }
        string? line;
        while ((line = outR.ReadLine()) != null) {
            var f = line.Split(',');
            var v = new double[idx.Length];
            for (int k = 0; k < idx.Length; k++)
                v[k] = idx[k] >= 0 && idx[k] < f.Length && double.TryParse(f[idx[k]], NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN;
            lock (_lock) {
                if (gen != _gen) return;
                if (_frames.Count < 100000) _frames.Add(v);
                if (mode >= 0 && mode < f.Length) _presentMode = f[mode];
                if (runtime >= 0 && runtime < f.Length) _runtime = f[runtime];
            }
        }
        Finished(pm, gen);
    }

    private void Finished(Process pm, int gen)
    {
        try { pm.WaitForExit(2000); } catch { }
        int code = -1;
        try { code = pm.ExitCode; } catch { }
        lock (_lock) {
            if (gen != _gen) return;  // a stopped capture: the next one owns the state
            string err = string.Join(" ", _stderr.Where(s => s.Contains("error", StringComparison.OrdinalIgnoreCase)));
            if (err.Contains("access denied", StringComparison.OrdinalIgnoreCase) || code == 6) {
                _state = "denied";
                _hint = InPerformanceLogUsers()
                    ? "access denied although this account is in Performance Log Users"
                    : "full mode needs admin rights or the \"Performance Log Users\" group (sign out and in after joining); run SVS Performance Monitor as admin meanwhile";
            } else if (_state != "denied") {
                _state = "exited";
                _hint = $"PresentMon stopped (exit {code}){(err.Length > 0 ? ": " + err : "")}";
            }
        }
    }

    private static double Mean(IEnumerable<double> xs)
    {
        double s = 0; int n = 0;
        foreach (var x in xs) if (!double.IsNaN(x)) { s += x; n++; }
        return n > 0 ? s / n : double.NaN;
    }

    private static double Pct(List<double> sorted, double p) =>
        sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(p / 100.0 * sorted.Count))];

    public FrameStats TakeWindow(double seconds)
    {
        List<double[]> fr;
        string state, hint, pmode, rt;
        lock (_lock) {
            fr = new List<double[]>(_frames);
            _frames.Clear();
            state = _state; hint = _hint; pmode = _presentMode; rt = _runtime;
        }
        var ft = fr.Select(f => f[0]).Where(x => !double.IsNaN(x) && x > 0).OrderBy(x => x).ToList();
        double sum = ft.Sum();
        // FPS = frames per second of frame time (not of the tick): robust when a tick holds a partial frame
        double fps = sum > 0 ? 1000.0 * ft.Count / sum : 0;
        // "1% low" = the frame rate of the slowest 1% of frames
        double low1 = ft.Count >= 1 ? 1000.0 / Mean(ft.Skip((int)(ft.Count * 0.99))) : double.NaN;
        return new FrameStats(state, hint, fr.Count, fps, Mean(ft), Pct(ft, 50), Pct(ft, 99), low1, Mean(fr.Select(f => f[1])), Mean(fr.Select(f => f[2])),
            Mean(fr.Select(f => f[3])), Mean(fr.Select(f => f[4])), Mean(fr.Select(f => f[5])), Mean(fr.Select(f => f[6])), pmode, rt);
    }

    // full-mode verdict: per frame, the side whose busy time fills the frame is the limit
    //   max(CPU busy, GPU busy) < 80% of the frame time -> neither (frame cap / vsync / waiting on something else)
    //   both within 10% of each other and both >= 80% -> both near the limit; else the bigger one
    public static Verdict? Judge(FrameStats f)
    {
        if (f.Frames == 0 || double.IsNaN(f.FrameTimeAvgMs) || double.IsNaN(f.CpuBusyMs) || double.IsNaN(f.GpuBusyMs)) return null;
        double ft = f.FrameTimeAvgMs, cb = f.CpuBusyMs, gb = f.GpuBusyMs;
        string nums = $"frame {ft:0.0} ms: CPU busy {cb:0.0} ms, GPU busy {gb:0.0} ms ({f.Fps:0} FPS)";
        if (Math.Max(cb, gb) < 0.8 * ft) return new Verdict("neither", nums + " - frame cap, vsync or waiting");
        if (Math.Abs(cb - gb) < 0.1 * ft && Math.Min(cb, gb) >= 0.8 * ft) return new Verdict("both near the limit", nums);
        return gb > cb ? new Verdict("GPU-bound", nums) : new Verdict("CPU-bound", nums);
    }

    private void StopCapture()
    {
        if (_pm != null) {
            lock (_lock) _gen++;  // its readers may still drain lines: they no longer count
            try { if (!_pm.HasExited) _pm.Kill(); } catch { }
            foreach (var t in _readers) t.Join(1000);  // the pipes close with the process; then dispose
            _readers.Clear();
            _pm.Dispose();
            _pm = null;
        }
    }

    public void Dispose()
    {
        StopCapture();
        if (_exe == null) return;
        // the ETW session must not outlive us, whatever state the capture was in (a 'starting' one holds a session too)
        EtwSessions.Stop(Session);
    }
}
