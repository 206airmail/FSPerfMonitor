// Program.cs - FSPerf: a performance monitor for Farming Simulator 25, from outside the game. Copyright (c) 2026 Jared Brown.
// MIT licence. See ..\README.md.
//   fsperf [--port 8765] [--interval-ms 1000] [--sample-hz 250] [--record <file.csv>] [--pid <n>] [--profile <dir>]
//          [--once] [--no-browser] [--presentmon <exe>] [--no-frames] [--lan]
//   --lan: the dashboard is also reachable from other computers (http://<this PC's IP>:<port>/); needs an inbound firewall
//          rule for the port (printed when missing). The page is read-only.
//   fsperf --burn <seconds>     (self-test helper: one busy main thread)
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FSPerf;

public sealed class Options
{
    public int Port = 8765;
    public int IntervalMs = 1000;
    public int SampleHz = 250;
    public string? Record;
    public int Pid;
    public string ProfileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "FarmingSimulator2025");
    public bool Once;
    public bool NoBrowser;
    public int Seconds;   // stop after this many seconds (0 = until Ctrl+C)
    public string? PresentMon;  // full mode: PresentMon console exe (default: next to fsperf.exe or in tools\)
    public bool NoFrames;       // basic mode only
    public bool Lan;            // listen on all interfaces, not only loopback

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++) {
            string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException($"{a[i]} needs a value");
            switch (a[i]) {
                case "--port": o.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--interval-ms": o.IntervalMs = Math.Max(200, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "--sample-hz": o.SampleHz = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--record": o.Record = Next(); break;
                case "--pid": o.Pid = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--profile": o.ProfileDir = Next(); break;
                case "--once": o.Once = true; break;
                case "--no-browser": o.NoBrowser = true; break;
                case "--seconds": o.Seconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--presentmon": o.PresentMon = Next(); break;
                case "--no-frames": o.NoFrames = true; break;
                case "--lan": o.Lan = true; break;
                default: throw new ArgumentException($"unknown option {a[i]}");
            }
        }
        return o;
    }
}

public static class Program
{
    public const string Version = "0.3.0";

    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--burn") return Burn(int.Parse(args[1], CultureInfo.InvariantCulture));
        if (args.Length == 4 && args[0] == "--judge") {  // self-test helper: the full-mode verdict for frame / CPU busy / GPU busy ms
            double P(string x) => double.Parse(x, CultureInfo.InvariantCulture);
            var f = new FrameStats("running", "", 100, 1000 / P(args[1]), P(args[1]), P(args[1]), P(args[1]), 0, P(args[2]), 0, P(args[3]), 0, 0, 0, "", "");
            Console.WriteLine(FrameMonitor.Judge(f)?.Label ?? "none");
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--stale-sessions") {  // self-test helper: stdin = `logman query -ets` text; args = live fsperf pids
            var live = args.Skip(1).Select(a => int.Parse(a, CultureInfo.InvariantCulture)).ToHashSet();
            foreach (var n in EtwSessions.Stale(EtwSessions.Parse(Console.In.ReadToEnd()), -1, live.Contains)) Console.WriteLine(n);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--is-fsperf-name") {  // self-test helper: would a live process of this name keep its ETW session?
            Console.WriteLine(EtwSessions.IsFsperfProcessName(args[1], "fsperf") ? "live" : "other");
            return 0;
        }
        if (args.Length == 1 && args[0] == "--logtail-selftest") return LogTailSelfTest.Run();   // self-test helper
        Options o;
        try { o = Options.Parse(args); } catch (Exception e) { Console.Error.WriteLine($"fsperf: {e.Message}"); return 2; }
        using var col = new Collector(o);
        if (o.Once) {
            col.Tick();
            Thread.Sleep(o.IntervalMs);
            Console.WriteLine(JsonSerializer.Serialize(col.Tick(), FsperfJson.Default.Snapshot));
            return 0;
        }
        using var rec = o.Record != null ? new CsvRecorder(o.Record) : null;
        using var tail = new LogTail(Path.Combine(o.ProfileDir, "log.txt"));
        using var web = new WebServer(o.Port, o.Lan, tail);
        web.Start();
        tail.Start();
        string url = $"http://127.0.0.1:{o.Port}/";
        Console.WriteLine($"FSPerf {Version}: dashboard {url}  (Ctrl+C to stop){(rec != null ? "  recording " + o.Record : "")}");
        if (o.Lan) {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses) {
                    var ip = ua.Address;
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip) && !ip.ToString().StartsWith("169.254."))
                        Console.WriteLine($"  on the network: http://{ip}:{o.Port}/");
                }
            }
            Console.WriteLine($"  other computers need an inbound firewall rule for TCP {o.Port} (admin PowerShell, once):");
            Console.WriteLine($"  New-NetFirewallRule -DisplayName 'FSPerf dashboard' -Direction Inbound -Protocol TCP -LocalPort {o.Port} -Action Allow -Profile Private -RemoteAddress LocalSubnet");
        }
        if (!o.NoBrowser) {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
        var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        var until = o.Seconds > 0 ? DateTime.UtcNow.AddSeconds(o.Seconds) : DateTime.MaxValue;
        try { col.Tick(); } catch { }
        string lastError = "";
        while (!stop.Wait(o.IntervalMs) && DateTime.UtcNow < until) {
            try {
                var s = col.Tick();
                web.Publish(JsonSerializer.Serialize(s, FsperfJson.Default.Snapshot));
                rec?.Write(s);
                lastError = "";
            } catch (Exception e) {   // one bad sample never ends the monitor (the game comes and goes all day)
                if (e.Message != lastError) Console.Error.WriteLine($"FSPerf: sample failed ({e.GetType().Name}: {e.Message}); retrying");
                lastError = e.Message;
            }
        }
        return 0;
    }

    // a process whose main thread spins: the self-test attaches to it (--pid) and must see ~100% main-thread busy
    private static int Burn(int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        double x = 0;
        while (DateTime.UtcNow < end) for (int i = 0; i < 100000; i++) x += Math.Sqrt(i);
        return x > 0 ? 0 : 1;
    }
}

public sealed class CsvRecorder : IDisposable
{
    private readonly StreamWriter _w;

    public CsvRecorder(string path)
    {
        _w = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
        _w.WriteLine("time,attached,pid,mode,verdict,main_thread_busy_pct,process_cpu_pct,gpu_3d_pct,gpu_dedicated_mb,working_set_mb,private_mb," +
                     "frames,fps,frame_ms_avg,frame_ms_p99,low1_fps,cpu_busy_ms,cpu_wait_ms,gpu_busy_ms,gpu_wait_ms,display_latency_ms," +
                     "main_thread_samples,top_modules,fsp_version,fsp_plugins");
    }

    private static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    private static string N(double v) => double.IsNaN(v) ? "" : v.ToString("0.##", CultureInfo.InvariantCulture);

    public void Write(Snapshot s)
    {
        string mods = string.Join("; ", s.MainThreadModules.Take(6).Select(m => $"{m.Name}={m.Percent.ToString("0.#", CultureInfo.InvariantCulture)}%"));
        string plugins = string.Join("; ", s.Fsp.Plugins.Select(p => $"{p.Name}:{p.State}"));
        var f = s.Frames != null && s.Frames.Frames > 0 ? s.Frames : null;
        _w.WriteLine(string.Join(",", s.Time.ToString("o", CultureInfo.InvariantCulture), s.Attached ? "1" : "0", s.Pid.ToString(CultureInfo.InvariantCulture),
            Q(s.Mode), Q(s.Verdict.Label), N(s.MainThreadBusyPct), N(s.ProcessCpuPct), N(s.Gpu3DPct), N(s.GpuDedicatedMb), N(s.WorkingSetMb), N(s.PrivateMb),
            (f?.Frames ?? 0).ToString(CultureInfo.InvariantCulture), N(f?.Fps ?? double.NaN), N(f?.FrameTimeAvgMs ?? double.NaN), N(f?.FrameTimeP99Ms ?? double.NaN),
            N(f?.Low1PctFps ?? double.NaN), N(f?.CpuBusyMs ?? double.NaN), N(f?.CpuWaitMs ?? double.NaN), N(f?.GpuBusyMs ?? double.NaN), N(f?.GpuWaitMs ?? double.NaN),
            N(f?.DisplayLatencyMs ?? double.NaN),
            s.MainThreadSamples.ToString(CultureInfo.InvariantCulture), Q(mods), Q(s.Fsp.Version), Q(plugins)));
    }

    public void Dispose() => _w.Dispose();
}
