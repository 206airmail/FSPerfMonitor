// Collector.cs - one snapshot per tick: finds the game, measures its CPU (process + main thread + busiest threads), GPU,
// memory, the main thread's module shares (MainThreadSampler), FSPluginHost's plugin status, and a CPU/GPU-bound verdict.
// Copyright (c) 2026 Jared Brown. MIT licence.
using System.Diagnostics;

namespace FSPerf;

public sealed record ThreadLoad(int Tid, double BusyPct, bool Main);

public sealed record Verdict(string Label, string Reason);

public sealed record Snapshot(
    DateTime Time, string Mode, bool Attached, int Pid, string Process, double ProcessCpuPct, int Cores, double MainThreadBusyPct,
    List<ThreadLoad> TopThreads, double Gpu3DPct, double GpuDedicatedMb, double WorkingSetMb, double PrivateMb,
    List<MainThreadSampler.Share> MainThreadModules, long MainThreadSamples, FspStatusInfo Fsp, Verdict Verdict, string Note);

public sealed class Collector : IDisposable
{
    public static readonly string[] GameNames = { "FarmingSimulator2025Game", "dedicatedServer" };
    private readonly Options _o;
    private readonly ModuleMap _modules = new();
    private readonly MainThreadSampler _sampler;
    private readonly GpuCounters _gpu = new();
    private Process? _proc;
    private IntPtr _procHandle;
    private int _mainTid;
    private DateTime _lastModules = DateTime.MinValue;
    private TimeSpan _lastProcCpu;
    private DateTime _lastTick;
    private readonly Dictionary<int, long> _lastThreadCpu = new();

    public Collector(Options o)
    {
        _o = o;
        _sampler = new MainThreadSampler(_modules, o.SampleHz);
    }

    private Process? FindGame()
    {
        if (_o.Pid > 0) {
            try { return Process.GetProcessById(_o.Pid); } catch { return null; }
        }
        // the game itself: FarmingSimulator2025Game.exe (the dedicated server's web admin dedicatedServer.exe starts it too)
        foreach (var p in Process.GetProcessesByName("FarmingSimulator2025Game")) return p;
        return null;
    }

    private bool Attach()
    {
        Detach();
        _proc = FindGame();
        if (_proc == null) return false;
        _procHandle = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ, false, (uint)_proc.Id);
        if (_procHandle == IntPtr.Zero) _procHandle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)_proc.Id);
        // the main thread = the oldest thread of the process (the game's update + render submission run on it)
        try {
            _mainTid = _proc.Threads.Cast<ProcessThread>().OrderBy(t => { try { return t.StartTime; } catch { return DateTime.MaxValue; } }).First().Id;
        } catch { _mainTid = 0; }
        _modules.Refresh(_procHandle, Path.GetFileName(SafeMainModule(_proc)));
        _lastModules = DateTime.UtcNow;
        if (_mainTid != 0) _sampler.Start((uint)_mainTid);
        _lastProcCpu = SafeCpu(_proc);
        _lastTick = DateTime.UtcNow;
        _lastThreadCpu.Clear();
        return true;
    }

    private static string SafeMainModule(Process p)
    {
        try { return p.MainModule?.FileName ?? p.ProcessName + ".exe"; } catch { return p.ProcessName + ".exe"; }
    }

    private static TimeSpan SafeCpu(Process p)
    {
        try { p.Refresh(); return p.TotalProcessorTime; } catch { return TimeSpan.Zero; }
    }

    private void Detach()
    {
        _sampler.Stop();
        if (_procHandle != IntPtr.Zero) Native.CloseHandle(_procHandle);
        _procHandle = IntPtr.Zero;
        _proc?.Dispose();
        _proc = null;
        _mainTid = 0;
    }

    public Snapshot Tick()
    {
        var now = DateTime.UtcNow;
        var fsp = FspStatus.Read(_o.ProfileDir);
        if (_proc == null || _proc.HasExited) {
            if (!Attach()) {
                return new Snapshot(DateTime.Now, "basic", false, 0, "", 0, Environment.ProcessorCount, 0, new(), double.NaN, double.NaN, 0, 0,
                    new(), 0, fsp, new Verdict("waiting", "the game is not running"), "");
            }
            now = DateTime.UtcNow;
        }
        var p = _proc!;
        double wall = Math.Max(0.001, (now - _lastTick).TotalSeconds);
        var cpu = SafeCpu(p);
        double procPct = 100.0 * (cpu - _lastProcCpu).TotalSeconds / wall / Environment.ProcessorCount;
        _lastProcCpu = cpu;
        _lastTick = now;

        // per-thread busy % (of one core) from kernel + user time deltas
        var loads = new List<ThreadLoad>();
        double mainBusy = 0;
        try {
            foreach (ProcessThread t in p.Threads) {
                long total;
                try { total = t.TotalProcessorTime.Ticks; } catch { continue; }
                if (_lastThreadCpu.TryGetValue(t.Id, out long before)) {
                    double pct = Math.Min(100, 100.0 * (total - before) / 1e7 / wall);
                    loads.Add(new ThreadLoad(t.Id, pct, t.Id == _mainTid));
                    if (t.Id == _mainTid) mainBusy = pct;
                }
                _lastThreadCpu[t.Id] = total;
            }
        } catch { }
        var top = loads.OrderByDescending(l => l.BusyPct).Take(8).ToList();

        if ((now - _lastModules).TotalSeconds > 5) {  // plugins and drivers can load late
            _modules.Refresh(_procHandle, Path.GetFileName(SafeMainModule(p)));
            _lastModules = now;
        }
        var (shares, samples) = _sampler.TakeWindow();
        var (gpu3d, vram) = _gpu.Sample(p.Id);
        double ws = 0, priv = 0;
        try { p.Refresh(); ws = p.WorkingSet64 / 1048576.0; priv = p.PrivateMemorySize64 / 1048576.0; } catch { }
        var verdict = Judge(mainBusy, gpu3d);
        string note = _sampler.Running ? "" : "main-thread sampler not running (thread access denied?)";
        return new Snapshot(DateTime.Now, "basic", true, p.Id, p.ProcessName, procPct, Environment.ProcessorCount, mainBusy, top, gpu3d, vram, ws,
            priv, shares, samples, fsp, verdict, note);
    }

    // basic mode (no frame events): the main thread's busy share against the GPU's 3D load.
    //   main >= 90 and GPU < 85 -> CPU-bound;  GPU >= 90 and main < 85 -> GPU-bound;  both high -> both near the limit;
    //   both low -> neither (frame cap, vsync, loading or paused)
    public static Verdict Judge(double mainBusy, double gpu3d)
    {
        if (double.IsNaN(gpu3d))
            return mainBusy >= 90 ? new Verdict("CPU-bound", $"main thread {mainBusy:0}% busy (no GPU counters)")
                                  : new Verdict("unknown", "no GPU counters on this machine");
        bool cpuHigh = mainBusy >= 90, gpuHigh = gpu3d >= 90;
        if (cpuHigh && gpu3d < 85) return new Verdict("CPU-bound", $"main thread {mainBusy:0}% busy, GPU 3D only {gpu3d:0}%");
        if (gpuHigh && mainBusy < 85) return new Verdict("GPU-bound", $"GPU 3D {gpu3d:0}%, main thread {mainBusy:0}% busy (waits on the GPU)");
        if (cpuHigh && gpuHigh) return new Verdict("both near the limit", $"main thread {mainBusy:0}%, GPU 3D {gpu3d:0}%");
        if (mainBusy < 85 && gpu3d < 85) return new Verdict("neither", $"main thread {mainBusy:0}%, GPU 3D {gpu3d:0}%: frame cap, vsync, menu or loading");
        return new Verdict("mixed", $"main thread {mainBusy:0}%, GPU 3D {gpu3d:0}%");
    }

    public void Dispose()
    {
        Detach();
        _gpu.Dispose();
    }
}
