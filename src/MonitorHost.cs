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
    public ShareClient.LogRequest? LogRequest => _share?.Request;   // #16
    public string? SendFullLogs(string requestId) => _share is null ? "this PC isn't sharing with a group." : _share.SendFullLogs(requestId);   // null = sent
    public void DeclineFullLogs() => _share?.DeclineFullLogs();
    private readonly string _shareProblem = "";

    // #15: every log the pages can show (the game's + FSPluginHost's), each followed by its own tail; looked for again
    // every few seconds (plugin logs appear when the game starts). Tails are never dropped while the monitor runs.
    private readonly object _logsLock = new();
    private readonly Dictionary<string, (LogSource Src, LogTail Tail)> _logs = new();
    private DateTime _logsAt = DateTime.MinValue;

    public LogTail? TailFor(string id) { lock (_logsLock) return _logs.TryGetValue(id, out var l) ? l.Tail : null; }

    public List<(LogSource Src, LogTail Tail)> Logs()
    {
        lock (_logsLock) return _logs.Values.OrderBy(l => Order(l.Src.Id)).ThenBy(l => l.Src.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int Order(string id) => id == LogSources.Game ? 0 : id == "fsp" ? 1 : id == "loader" ? 2 : 3;

    private void RefreshLogs(FspStatusInfo? fsp)
    {
        _logsAt = DateTime.UtcNow;
        List<LogSource> found;
        try { found = LogSources.Find(_o.ProfileDir, fsp); } catch { return; }
        List<LogTail> retired;
        lock (_logsLock) {
            var ids = found.Select(f => f.Id).ToHashSet();
            retired = _logs.Where(kv => kv.Key != LogSources.Game && !ids.Contains(kv.Key)).Select(kv => kv.Value.Tail).ToList();
            foreach (var k in _logs.Keys.Where(k => k != LogSources.Game && !ids.Contains(k)).ToList()) _logs.Remove(k);
            foreach (var src in found) {
                if (_logs.ContainsKey(src.Id) || _logs.Count >= LogSources.Max) continue;
                var t = new LogTail(src.Path, ringSize: 3000);
                _logs[src.Id] = (src, t);
                t.Start();
            }
        }
        foreach (var t in retired) t.Dispose();
    }

    // throws when the dashboard port can't be opened (e.g. in use): the caller reports it
    public MonitorHost(Options o, ISettingsBackend? settings = null)
    {
        _o = o;
        _col = new Collector(o);
        try {
            _rec = o.Record != null ? new CsvRecorder(o.Record) : null;
            _tail = new LogTail(Path.Combine(o.ProfileDir, "log.txt"));
            _logs[LogSources.Game] = (new LogSource(LogSources.Game, LogSources.LabelFor(LogSources.Game), Path.Combine(o.ProfileDir, "log.txt")), _tail);
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
                if (relay != null) { _share = new ShareClient(relay, o.ShareCode, Logs, () => _latest, o.Port); }
                else _shareProblem = why ?? "bad address";
            }
        } catch {
            _rec?.Dispose();
            _col.Dispose();
            throw;
        }
        _tail.Start();
        RefreshLogs(FspStatus.Read(o.ProfileDir));
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
                if ((DateTime.UtcNow - _logsAt).TotalSeconds >= 5) RefreshLogs(s.Fsp);
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
        lock (_logsLock) foreach (var l in _logs.Values) l.Tail.Dispose();   // the game's tail too
        _rec?.Dispose();
        _col.Dispose();   // stops PresentMon and its SVSPerf_<pid> ETW session
    }
}
