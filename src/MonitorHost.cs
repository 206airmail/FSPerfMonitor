// MonitorHost.cs - the running monitor: collector + dashboard web server + game-log tail (+ optional CSV), ticking on its
// own thread. Shared by the command line and the tray app, so both start and, above all, STOP the same way: Dispose()
// ends the tick thread and disposes the collector, which stops PresentMon and its ETW session (a kill would leave it).
// Copyright (c) 2026 Jared Brown. MIT licence.
using System.Text.Json;

namespace FSPerf;

public sealed class MonitorHost : IDisposable
{
    private readonly Options _o;
    private readonly Collector _col;
    private readonly LogTail _tail;
    private readonly WebServer _web;
    private readonly CsvRecorder? _rec;
    private readonly RelayServer? _relay;
    private readonly ShareClient? _share;
    private readonly ManualResetEventSlim _stop = new();
    private Thread? _thread;
    private volatile Snapshot? _latest;
    private int _disposed;

    public string Url => $"http://127.0.0.1:{_o.Port}/";
    public Snapshot? Latest => _latest;
    public event Action<string>? Error;   // a failed tick (logged by the caller; the monitor keeps going)

    // #14: the group this PC hosts (null = none) and why it isn't running when it should; the sharing state
    public RelayMembers? GroupMembers { get; }
    public string GroupProblem { get; } = "";
    public string ShareState => _share?.State ?? (_shareProblem.Length > 0 ? "bad-address" : "off");
    public string ShareName => _share?.MemberName ?? "";
    private readonly string _shareProblem = "";

    // throws when the dashboard port can't be opened (e.g. in use): the caller reports it
    public MonitorHost(Options o, ISettingsBackend? settings = null)
    {
        _o = o;
        _col = new Collector(o);
        try {
            _rec = o.Record != null ? new CsvRecorder(o.Record) : null;
            _tail = new LogTail(Path.Combine(o.ProfileDir, "log.txt"));
            _web = new WebServer(o.Port, o.Lan, _tail) { Settings = settings, Host = this };
            _web.Start();
            // the group relay: a busy port must not stop the monitor itself (the Settings page says why)
            if (o.GroupPort > 0) {
                GroupMembers = new RelayMembers(o.GroupMembers ?? RelayMembers.DefaultFile);
                try { _relay = new RelayServer(o.GroupPort, GroupMembers); _relay.Start(); }
                catch (System.Net.Sockets.SocketException) { _relay?.Dispose(); _relay = null; GroupProblem = $"Port {o.GroupPort} is already used by another program. Choose another."; }
            }
            if (!string.IsNullOrEmpty(o.ShareUrl) && !string.IsNullOrEmpty(o.ShareCode)) {
                var relay = ShareClient.ParseAddress(o.ShareUrl, out string? why);
                if (relay != null) { _share = new ShareClient(relay, o.ShareCode, _tail, () => _latest, o.Port); }
                else _shareProblem = why ?? "bad address";
            }
        } catch {
            _rec?.Dispose();
            _col.Dispose();
            throw;
        }
        _tail.Start();
        _share?.Start();
    }

    public void Start()
    {
        _thread ??= new Thread(Loop) { IsBackground = true, Name = "svsperf-monitor" };
        _thread.Start();
    }

    private void Loop()
    {
        var until = _o.Seconds > 0 ? DateTime.UtcNow.AddSeconds(_o.Seconds) : DateTime.MaxValue;
        try { _latest = _col.Tick(); } catch { }
        string lastError = "";
        while (!_stop.Wait(_o.IntervalMs) && DateTime.UtcNow < until) {
            try {
                var s = _col.Tick();
                _latest = s;
                _web.Publish(JsonSerializer.Serialize(s, FsperfJson.Default.Snapshot));
                _rec?.Write(s);
                lastError = "";
            } catch (Exception e) {   // one bad sample never ends the monitor (the game comes and goes all day)
                if (e.Message != lastError) Error?.Invoke($"sample failed ({e.GetType().Name}: {e.Message}); retrying");
                lastError = e.Message;
            }
        }
        _stop.Set();   // a --seconds run ended by itself: WaitForStop returns
    }

    // true when the monitor stopped by itself (--seconds) within the timeout
    public bool WaitForStop(int ms) => _stop.Wait(ms);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Set();
        _thread?.Join(5000);
        _share?.Dispose();   // tells the group at once that sharing stopped
        _relay?.Dispose();
        _web.Dispose();
        _tail.Dispose();
        _rec?.Dispose();
        _col.Dispose();   // stops PresentMon and its SVSPerf_<pid> ETW session
    }
}
