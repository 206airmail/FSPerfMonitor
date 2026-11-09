// Program.cs - FSPerf: a performance monitor for Farming Simulator 25, from outside the game. Copyright (c) 2026 Jared Brown.
// MIT licence. See ..\README.md.
//   SVSPerfMonitor [--port 8765] [--interval-ms 1000] [--sample-hz 0|250] [--record <file.csv>] [--pid <n>] [--profile <dir>]
//          [--once] [--no-browser] [--presentmon <exe>] [--no-frames] [--lan]
//   SVSPerfMonitor            (no arguments, e.g. a double-click) or --tray [options]: the tray app, one copy per user
//   SVSPerfMonitor --exit     asks the running tray app to quit cleanly (stops its frame capture)
//   --lan: the dashboard is also reachable from other computers (http://<this PC's IP>:<port>/); needs an inbound firewall
//          rule for the port (printed when missing). The page is read-only.
//   SVSPerfMonitor --burn <seconds>     (self-test helper: one busy main thread)
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FSPerf;

public sealed class Options
{
    public int Port = 8765;
    public int IntervalMs = 1000;
    public int SampleHz;        // main-thread breakdown: 0 = off (default: it pauses the game's main thread), e.g. 250
    public string? Record;
    public int Pid;
    public string ProfileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "FarmingSimulator2025");
    public bool Once;
    public bool NoBrowser;
    public int Seconds;   // stop after this many seconds (0 = until Ctrl+C)
    public string? PresentMon;  // full mode: PresentMon console exe (default: next to SVSPerfMonitor.exe or in tools\)
    public bool NoFrames;       // basic mode only
    public bool Lan;            // listen on all interfaces, not only loopback
    public bool Tray;           // the tray app (also: no arguments at all, i.e. a double-click)
    public bool Exit;           // ask the running tray app to quit cleanly
    public string Instance = "SVSPerfMonitor";   // single-instance name (the self-test uses its own)
    public string? SettingsFile;                 // the tray app's settings file (default %APPDATA%; the self-test uses its own)
    public int GroupPort;                        // #14: host a group relay on 127.0.0.1:<port> (0 = no)
    public string? GroupMembers;                 // its members file (default next to the settings; the self-test uses its own)
    public string? ShareUrl;                     // #14: share with the group relay at this address
    public string? ShareCode;                    //      using this invite code
    public string ServerMode = "auto";           // #17: auto (from the game's command line) | on | off

    // the command line, on top of `start` (the tray app's settings) or the defaults
    public static Options Parse(string[] a, Options? start = null)
    {
        var o = start ?? new Options();
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
                case "--tray": o.Tray = true; break;
                case "--exit": o.Exit = true; break;
                case "--instance": o.Instance = Next(); break;
                case "--tray-child": break;   // internal marker (Main)
                case "--settings": o.SettingsFile = Next(); break;
                case "--autostart": o.NoBrowser = true; break;   // started by Windows / handed over: no browser
                case "--handover": break;                        // internal: wait for the copy that hands over (Tray)
                case "--group-port": o.GroupPort = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--group-members": o.GroupMembers = Next(); break;
                case "--share": o.ShareUrl = Next(); break;
                case "--share-code": o.ShareCode = Next(); break;
                case "--server-mode": {
                    string m = Next();
                    o.ServerMode = m is "on" or "off" or "auto" ? m : throw new ArgumentException("--server-mode takes auto, on or off");
                    break;
                }
                default: throw new ArgumentException($"unknown option {a[i]}");
            }
        }
        return o;
    }
}

public static class Program
{
    public static readonly string Version = typeof(Program).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";  // = csproj <Version> (#11)

    private const string Usage = """
        SVS Performance Monitor - what limits Farming Simulator 25: processor or graphics card (SimVizion Studios)

          SVSPerfMonitor                 the tray app (also a double-click on SVSPerfMonitor.exe); settings in its menu
          SVSPerfMonitor --exit          stop the running tray app cleanly
          SVSPerfMonitor --once          one JSON snapshot to stdout, for scripts and test runners
          SVSPerfMonitor --record x.csv  a dashboard on the command line, plus one CSV line per second (Ctrl+C stops)
          SVSPerfMonitor --no-browser    the same without a CSV

          options: --port <n>  --lan  --interval-ms <ms>  --sample-hz <hz> (main-loop breakdown, 0 = off)
                   --pid <n>  --profile <folder>  --seconds <n>  --presentmon <exe>  --no-frames  --tray
                   --server-mode auto|on|off (a dedicated server: found from the game's -server, or forced)
                   group: --group-port <n> (host a group relay on this PC, behind an HTTPS web server)
                          --share <address> --share-code <invite code> (share with a group)
                          --group-add <name> / --group-list / --group-remove <id> (manage the members)
        The command line never reads the tray app's settings. Help and source: https://github.com/206airmail/FSPerfMonitor
        """;
    private const string TrayChildArg = "--tray-child";   // internal: the console-less copy the tray app restarted as

    public static int Main(string[] args)
    {
        bool tray = args.Length == 0 || args.Contains("--tray") || args.Contains(TrayChildArg);
        // the tray app never keeps a console: started from a terminal it would block it (a console program), and on Windows
        // before 11 24H2 a double-click opens a console window. Then start a console-less copy and leave (a brief flash).
        if (tray && !args.Contains(TrayChildArg) && TrayNative.GetConsoleWindow() != IntPtr.Zero) {
            // no inherited handles: a shell that captures our output must not wait for the tray copy
            if (TrayNative.StartDetached(Environment.ProcessPath!, [.. args, TrayChildArg])) return 0;
        }
        args = args.Where(a => a != TrayChildArg).ToArray();
        // (more arguments are ignored: the self-test adds -server to look like a dedicated server, #17)
        if (args.Length >= 2 && args[0] == "--burn") return Burn(int.Parse(args[1], CultureInfo.InvariantCulture));
        if (args.Length == 1 && args[0] == "--is-server-cmdline") { Console.WriteLine(Collector.IsServerCommandLine(Console.In.ReadToEnd().Trim()) ? "server" : "game"); return 0; }   // self-test helper (#17)
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
            Console.WriteLine(EtwSessions.IsFsperfProcessName(args[1], "SVSPerfMonitor") ? "live" : "other");
            return 0;
        }
        if (args.Length == 1 && args[0] == "--logtail-selftest") return LogTailSelfTest.Run();   // self-test helper
        if (args.Length >= 2 && args[0] == "--admin") return AutoStart.AdminMain(args);   // the elevated helper (#7)
        if (args.Length == 1 && args[0] is "--help" or "-h" or "/?") { Console.WriteLine(Usage); return 0; }
        if (args.Length == 3 && args[0] == "--task-xml") {   // self-test helper: the scheduled task's definition
            Console.WriteLine(AutoStart.TaskXml(AutoStart.CurrentUserSid(), AutoStart.StarterPath(), args[1] == "1", args[2]));
            return 0;
        }
        if (args.Length >= 1 && args[0] is "--group-add" or "--group-list" or "--group-remove") return GroupCommand(args);
        if (args.Length == 1 && args[0] == "--redact") { Console.Write(Redactor.Apply(Console.In.ReadToEnd())); return 0; }   // self-test helper (#14): stdin -> stdout
        if (args.Length == 1 && args[0] == "--frametimes-access") { Console.WriteLine(AutoStart.FrameTimesAccess()); return 0; }   // self-test helper
        Options o;
        try { o = Options.Parse(args); } catch (Exception e) { Console.Error.WriteLine($"SVSPerfMonitor: {e.Message}"); return 2; }
        if (o.Exit) {
            int r = TrayApp.SignalExit(o.Instance);
            Console.WriteLine(r == 0 ? "SVS Performance Monitor: stopped" : r == 1 ? "SVS Performance Monitor: not running" : r == 3 ? "SVS Performance Monitor runs with full permissions: use Exit in its tray menu, or run this as admin" : "SVS Performance Monitor: did not stop in time");
            return r;
        }
        if (tray) return TrayApp.Run(args);   // its options: the settings file, then this command line
        if (o.Once) {
            using var col = new Collector(o);
            col.Tick();
            Thread.Sleep(o.IntervalMs);
            Console.WriteLine(JsonSerializer.Serialize(col.Tick(), FsperfJson.Default.Snapshot));
            return 0;
        }
        MonitorHost host;
        try { host = new MonitorHost(o); }
        catch (System.Net.Sockets.SocketException e) { Console.Error.WriteLine($"SVSPerfMonitor: port {o.Port}: {e.Message}"); return 3; }
        using var _ = host;
        host.Error += m => Console.Error.WriteLine("SVSPerfMonitor: " + m);
        host.Start();
        string url = host.Url;
        Console.WriteLine($"SVS Performance Monitor {Version}: dashboard {url}  (Ctrl+C to stop){(o.Record != null ? "  recording " + o.Record : "")}");
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
            Console.WriteLine($"  New-NetFirewallRule -DisplayName 'SVS Performance Monitor dashboard' -Direction Inbound -Protocol TCP -LocalPort {o.Port} -Action Allow -Profile Private -RemoteAddress LocalSubnet");
        }
        if (!o.NoBrowser) TrayApp.OpenUrl(url);
        var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        while (!stop.Wait(200) && !host.WaitForStop(0)) { }
        return 0;   // disposing the host stops the monitor and the frame capture
    }

    // the group's members from the command line: `--group-add <name> [--group-members <file>]` prints the invite code
    private static int GroupCommand(string[] a)
    {
        string? file = null;
        var rest = new List<string>();
        for (int i = 0; i < a.Length; i++) {
            if (a[i] == "--group-members" && i + 1 < a.Length) file = a[++i];
            else if (a[i] == "--settings" && i + 1 < a.Length) SettingsStore.OverridePath = a[++i];
            else rest.Add(a[i]);
        }
        var members = new RelayMembers(file ?? RelayMembers.DefaultFile);
        switch (rest[0]) {
            case "--group-add" when rest.Count == 2: {
                var (m, code, problem) = members.Add(rest[1]);
                if (m == null) { Console.Error.WriteLine("SVSPerfMonitor: " + problem); return 1; }
                Console.WriteLine($"{m.Id} {m.Name} {code}");
                return 0;
            }
            case "--group-list" when rest.Count == 1:
                foreach (var m in members.All()) Console.WriteLine($"{m.Id} {m.Name}");
                return 0;
            case "--group-remove" when rest.Count == 2:
                return members.Remove(rest[1]) ? 0 : 1;
        }
        Console.Error.WriteLine("SVSPerfMonitor: --group-add <name> | --group-list | --group-remove <id>");
        return 2;
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
