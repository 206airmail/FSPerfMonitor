// WebServer.cs - the dashboard on http://127.0.0.1:<port>/ (loopback only, no admin rights, no URL reservations):
// GET / = the page, GET /api/snapshot = the last snapshot as JSON, GET /events = server-sent events, one per tick.
// 0.3: GET /log = the log page, GET /log/stream = the game's log.txt as server-sent events (see LogTail).
// A minimal HTTP/1.1 server on a TcpListener. Copyright (c) 2026 Jared Brown. MIT licence.
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace FSPerf;

// the tray app's side of the Settings page
public interface ISettingsBackend
{
    AppSettings Current { get; }
    string? Save(AppSettings s);   // null = saved; else the message to show
    void Saved();                  // called after the answer went out: apply them
}

public sealed class WebServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly List<StreamWriter> _clients = new();
    private readonly List<LogClient> _logClients = new();   // under _tail.Gate
    private readonly LogTail? _tail;
    private readonly byte[] _logPage;
    private const int MaxLogClients = 8;
    private const int MaxEventClients = 16;
    private const int RequestDeadlineMs = 10000;
    private int _reserved;  // live-view slots taken but not yet registered (under _lock)
    private readonly object _lock = new();
    private string _last = "{}";
    private readonly byte[] _page;
    private volatile bool _stop;

    // lan = also reachable from other computers (all interfaces); still no URL reservation (a raw TcpListener), but Windows
    // Firewall must allow the port inbound (Program prints the rule when it is missing)
    public WebServer(int port, bool lan = false, LogTail? tail = null)
    {
        _tail = tail;
        _port = port; _lanMode = lan;
        _hosts = BuildHosts(); _hostsAt = DateTime.UtcNow;
        if (tail != null) { tail.Appended += OnLogAppended; tail.Heartbeat += OnLogHeartbeat; }
        _listener = new TcpListener(lan ? IPAddress.Any : IPAddress.Loopback, port);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        _page = ms.ToArray();
        using var s2 = Assembly.GetExecutingAssembly().GetManifestResourceStream("log.html")!;
        using var ms2 = new MemoryStream();
        s2.CopyTo(ms2);
        _logPage = ms2.ToArray();
        using var s3 = Assembly.GetExecutingAssembly().GetManifestResourceStream("settings.html")!;
        using var ms3 = new MemoryStream();
        s3.CopyTo(ms3);
        _settingsPage = ms3.ToArray();
    }

    private readonly byte[] _settingsPage;

    public void Start()
    {
        _listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "fsperf-http" }.Start();
    }

    private void AcceptLoop()
    {
        while (!_stop) {
            TcpClient c;
            try { c = _listener.AcceptTcpClient(); } catch { return; }
            ThreadPool.QueueUserWorkItem(_ => Handle(c));
        }
    }

    private void Handle(TcpClient c)
    {
        try {
            var stream = c.GetStream();
            // a client that never finishes its request (or floods it) must not hold a worker or memory (--lan): every line
            // is read with a byte limit, the whole request within RequestDeadlineMs
            stream.ReadTimeout = 5000;
            var deadline = DateTime.UtcNow.AddMilliseconds(RequestDeadlineMs);
            string? line = ReadLineBounded(stream, 2048, deadline);
            if (line == null) { c.Close(); return; }
            int headers = 0, contentLength = 0;
            string lastEventId = "", hostHeader = "", origin = "", contentType = "";
            while (true) {
                string? h = ReadLineBounded(stream, 8192, deadline);
                if (h == null || ++headers > 100) { c.Close(); return; }
                if (h.Length == 0) break;  // end of the headers
                if (h.StartsWith("Last-Event-ID:", StringComparison.OrdinalIgnoreCase)) lastEventId = h[14..].Trim();
                else if (h.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) hostHeader = h[5..].Trim();
                else if (h.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase)) origin = h[7..].Trim();
                else if (h.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)) contentType = h[13..].Trim();
                else if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(h[15..].Trim(), out contentLength);
            }
            if (hostHeader.Length == 0) { Send(stream, 400, "text/plain", Encoding.ASCII.GetBytes("Host header required")); c.Close(); return; }
            if (!HostAllowed(hostHeader)) { Send(stream, 403, "text/plain", Encoding.ASCII.GetBytes("forbidden host")); c.Close(); return; }   // DNS rebinding guard
            string path = line.Split(' ').ElementAtOrDefault(1) ?? "/";
            string route = path.Split('?')[0];
            if (line.StartsWith("POST ") && route == "/api/settings") {
                HandleSaveSettings(c, stream, origin, contentType, contentLength, deadline);
                c.Close();
                return;
            }
            if (!line.StartsWith("GET ")) { Send(stream, 405, "text/plain", Encoding.ASCII.GetBytes("GET only")); c.Close(); return; }
            if (route == "/api/settings") {
                Send(stream, 200, "application/json", Encoding.UTF8.GetBytes(SettingsViewJson(IsLocal(c))));
                c.Close();
                return;
            }
            if (route == "/settings") { Send(stream, 200, "text/html; charset=utf-8", _settingsPage); c.Close(); return; }
            if (Assets.TryGetValue(route, out var asset)) { Send(stream, 200, asset.Type, asset.Body, cache: true); c.Close(); return; }
            if (path == "/events") {
                // reserve a live-view slot atomically (check + reserve under one lock), released when registered or failed
                lock (_lock) {
                    if (_clients.Count + _reserved >= MaxEventClients) {
                        Send(stream, 503, "text/plain", Encoding.ASCII.GetBytes("too many live views"));
                        c.Close();
                        return;
                    }
                    _reserved++;
                }
                bool added = false;
                try {
                    // Publish writes on the sampling thread: a client that stops reading (full socket buffer) must not stall
                    // sampling, so a write that takes longer than 2 s throws and Publish drops that client
                    stream.WriteTimeout = 2000;
                    var w = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    w.Write("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: keep-alive\r\n\r\n");
                    w.Write($"data: {_last}\n\n");
                    lock (_lock) { _reserved--; _clients.Add(w); added = true; }
                } finally {
                    if (!added) lock (_lock) _reserved--;
                }
                return;  // kept open; Publish writes to it
            }
            if (route == "/log/stream" && _tail != null) {
                ServeLogStream(c, stream, path, lastEventId);
                return;  // kept open; the tail writes to it
            }
            if (route == "/log") { Send(stream, 200, "text/html; charset=utf-8", _logPage); c.Close(); return; }
            if (path == "/api/snapshot") Send(stream, 200, "application/json", Encoding.UTF8.GetBytes(_last));
            else if (path == "/" || path.StartsWith("/?")) Send(stream, 200, "text/html; charset=utf-8", _page);
            else Send(stream, 404, "text/plain", Encoding.ASCII.GetBytes("not found"));
            c.Close();
        } catch {
            try { c.Close(); } catch { }
        }
    }

    // ---- the log stream -------------------------------------------------------------------------------------------
    // GET /log/stream?n=500[&after=<epoch>.<seq>]  (the Last-Event-ID header a reconnecting EventSource sends wins over ?after=).
    // Each event: `id: <epoch>.<last seq in it>` and data {"e":epoch,"reset":bool,"gap":bool,"lines":[[seq,text,marker],...]}.
    // reset = the client should drop what it shows (a fresh view); gap = lines between its last id and the first line here were
    // already pushed out of the ring.
    // Network I/O never happens under tail.Gate: every viewer has a small bounded queue and its own writer thread. A viewer
    // whose queue overflows, or whose write exceeds the per-event deadline, is closed (its browser reconnects with Last-Event-ID).
    private sealed class LogClient
    {
        public const int MaxQueued = 64, EventDeadlineMs = 5000, ChunkBytes = 16 * 1024;
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly Queue<object> _q = new();   // string (ready text) or HistoryItem
        private bool _closed;
        public LogClient(TcpClient tcp, NetworkStream stream) { _tcp = tcp; _stream = stream; }
        public bool Closed { get { lock (_q) return _closed; } }

        // never blocks: false = the client was dropped (queue full)
        public bool Enqueue(object item)
        {
            lock (_q) {
                if (_closed) return false;
                if (_q.Count >= MaxQueued) { CloseLocked(); return false; }
                _q.Enqueue(item);
                Monitor.Pulse(_q);
                return true;
            }
        }

        public void Close() { lock (_q) CloseLocked(); }
        private void CloseLocked()
        {
            if (_closed) return;
            _closed = true;
            Monitor.PulseAll(_q);
            try { _tcp.Close(); } catch { }   // unblocks a write in progress
        }

        public object? Next()
        {
            lock (_q) {
                while (_q.Count == 0 && !_closed) Monitor.Wait(_q);
                return _closed ? null : _q.Dequeue();
            }
        }

        public bool Write(string text)
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            var until = DateTime.UtcNow.AddMilliseconds(EventDeadlineMs);
            try {
                for (int off = 0; off < bytes.Length; off += ChunkBytes) {
                    if (DateTime.UtcNow > until) return false;
                    _stream.Write(bytes, off, Math.Min(ChunkBytes, bytes.Length - off));
                }
                return true;
            } catch { return false; }
        }
    }

    private sealed record HistoryItem(List<LogLine> Lines, bool Reset, bool Gap, long LastSeq);

    private void ServeLogStream(TcpClient c, NetworkStream stream, string path, string lastEventId)
    {
        var tail = _tail!;
        int n = 500;
        string after = "";
        int q = path.IndexOf('?');
        if (q >= 0) {
            foreach (var kv in path[(q + 1)..].Split('&')) {
                int eq = kv.IndexOf('=');
                if (eq < 0) continue;
                string k = kv[..eq], v = Uri.UnescapeDataString(kv[(eq + 1)..]);
                if (k == "n" && int.TryParse(v, out int nn)) n = Math.Clamp(nn, 0, tail.RingSize);
                else if (k == "after" && v.Length > 0) after = v;
            }
        }
        if (lastEventId.Length > 0) after = lastEventId;   // the browser's own reconnect id is always newer than a URL's
        long afterSeq = -1;
        bool resumed = false;
        int dot = after.IndexOf('.');
        if (dot > 0 && after[..dot] == tail.Epoch && long.TryParse(after[(dot + 1)..], out long a) && a >= 0) { afterSeq = a; resumed = true; }
        LogClient? client = null;
        try {
            stream.WriteTimeout = 2000;
            var cl = new LogClient(c, stream);
            bool full = false;
            lock (tail.Gate) {            // snapshot + register under one lock (no I/O in here): no gap, no duplicate
                if (_logClients.Count >= MaxLogClients) full = true;
                else {
                    if (resumed && afterSeq > tail.LastSeq) { afterSeq = -1; resumed = false; }
                    var lines = tail.Snapshot(afterSeq, n, out bool gap);
                    cl.Enqueue("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nX-Accel-Buffering: no\r\nConnection: keep-alive\r\n\r\nretry: 2000\n\n");
                    // the game restarted after this viewer's last line, but the marker already left the ring (Codex #46): the
                    // batch cannot carry the boundary, so tell the page to drop what it shows (reset) instead
                    long mk = tail.LastMarkerSeq;
                    bool lostRestart = resumed && mk > afterSeq && lines.Count > 0 && lines[0].Seq > mk;
                    if (lines.Count > 0 || !resumed) cl.Enqueue(new HistoryItem(lines, !resumed || lostRestart, gap, lines.Count > 0 ? lines[^1].Seq : tail.LastSeq));
                    _logClients.Add(cl);
                    client = cl;
                }
            }
            if (full) {
                Send(stream, 503, "text/plain", Encoding.ASCII.GetBytes("too many log views"));
                c.Close();
                return;
            }
            new Thread(() => LogWriter(client!)) { IsBackground = true, Name = "fsperf-logclient" }.Start();
        } catch {
            try { c.Close(); } catch { }
        }
    }

    private void LogWriter(LogClient cl)
    {
        try {
            while (true) {
                object? item = cl.Next();
                if (item == null) break;
                string text = item is HistoryItem h ? LogEvent(_tail!, h.Lines, h.Reset, h.Gap, h.LastSeq) : (string)item;
                if (!cl.Write(text)) break;
            }
        } catch { }
        cl.Close();
        lock (_tail!.Gate) _logClients.Remove(cl);
    }

    private static string LogEvent(LogTail tail, IReadOnlyList<LogLine> lines, bool reset, bool gap, long lastSeq)
    {
        var sb = new StringBuilder(64 + lines.Count * 120);
        sb.Append("id: ").Append(tail.Epoch).Append('.').Append(lastSeq).Append("\ndata: {\"e\":\"").Append(tail.Epoch).Append("\",\"reset\":")
          .Append(reset ? "true" : "false").Append(",\"gap\":").Append(gap ? "true" : "false").Append(",\"lines\":[");
        for (int i = 0; i < lines.Count; i++) {
            if (i > 0) sb.Append(',');
            sb.Append('[').Append(lines[i].Seq).Append(',').Append(System.Text.Json.JsonSerializer.Serialize(lines[i].Text, FsperfJson.LogText))
              .Append(lines[i].Marker ? ",1]" : ",0]");
        }
        return sb.Append("]}\n\n").ToString();
    }


    // both run under tail.Gate on the tailer's thread: they only queue, never touch a socket
    private void OnLogAppended(IReadOnlyList<LogLine> lines)
    {
        if (_logClients.Count == 0) return;
        FanOut(LogEvent(_tail!, lines, false, false, lines[^1].Seq));
    }

    private void OnLogHeartbeat() { if (_logClients.Count > 0) FanOut(": keep-alive\n\n"); }

    private void FanOut(string text)
    {
        for (int i = _logClients.Count - 1; i >= 0; i--) {
            if (!_logClients[i].Enqueue(text)) _logClients.RemoveAt(i);   // full queue = dropped
        }
    }

    // ---- Host header check (DNS rebinding): every route -------------------------------------------------------------
    private readonly int _port;
    private readonly bool _lanMode;
    private HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _hostsAt = DateTime.MinValue;

    private HashSet<string> BuildHosts()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"127.0.0.1:{_port}", $"localhost:{_port}", $"[::1]:{_port}" };
        if (_lanMode) {
            try {
                set.Add($"{Dns.GetHostName()}:{_port}");
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses) {
                        var ip = ua.Address;
                        if (ip.AddressFamily == AddressFamily.InterNetwork) set.Add($"{ip}:{_port}");
                        else if (ip.AddressFamily == AddressFamily.InterNetworkV6) set.Add($"[{new IPAddress(ip.GetAddressBytes())}]:{_port}");
                    }
            } catch { }
        }
        return set;
    }

    public bool HostAllowed(string host)
    {
        lock (_lock) {
            if (_hosts.Contains(host)) return true;
            if (!_lanMode || (DateTime.UtcNow - _hostsAt).TotalSeconds < 30) return false;   // addresses change: refresh, at most every 30 s
            _hosts = BuildHosts(); _hostsAt = DateTime.UtcNow;
            return _hosts.Contains(host);
        }
    }

    // one CRLF / LF terminated ASCII line of at most max bytes, read byte by byte (requests are tiny); null = too long, the
    // connection closed, or the request deadline passed
    private static string? ReadLineBounded(NetworkStream s, int max, DateTime deadline)
    {
        var sb = new StringBuilder();
        while (true) {
            int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (left <= 0) return null;
            s.ReadTimeout = Math.Min(5000, left);  // each wait ends by the deadline at the latest
            int b = s.ReadByte();
            if (b < 0 || DateTime.UtcNow > deadline) return null;
            if (b == '\n') return sb.ToString().TrimEnd('\r');
            if (sb.Length >= max) return null;
            sb.Append((char)b);
        }
    }

    private static void Send(NetworkStream s, int code, string type, byte[] body, bool cache = false)
    {
        string reason = code switch {
            200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", 413 => "Payload Too Large",
            415 => "Unsupported Media Type", 422 => "Unprocessable Content", 503 => "Service Unavailable", _ => "Method Not Allowed",
        };
        // the page's style sheet, logo and icons may be cached briefly; everything with live data never
        string cc = cache ? "max-age=300" : "no-store";
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: {cc}\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        s.Write(head);
        s.Write(body);
    }

    // ---- settings (the tray app) ------------------------------------------------------------------------------------
    // GET /api/settings: the settings + whether this viewer may change them. POST /api/settings: save, from THIS PC only:
    // the connection comes from a loopback address, the request is JSON (a page on another site can't send that without a
    // CORS preflight, which this server never answers) and its Origin is this dashboard. A phone on the home network
    // (--lan) sees the settings read-only.
    public ISettingsBackend? Settings { get; set; }

    private static bool IsLocal(TcpClient c) =>
        c.Client.RemoteEndPoint is IPEndPoint ep && IPAddress.IsLoopback(ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address);

    private bool OriginIsThisDashboard(string origin) =>
        origin == $"http://127.0.0.1:{_port}" || origin == $"http://localhost:{_port}" || origin == $"http://[::1]:{_port}";

    private string SettingsViewJson(bool local)
    {
        var b = Settings;
        string why = b == null ? "These settings belong to the tray app. This copy was started from the command line."
            : !local ? "Settings can be changed on the PC running SVS Performance Monitor." : "";
        var view = new SettingsView(b?.Current ?? new AppSettings(), b != null && local, why, SettingsStore.DefaultProfileFolder,
            Program.Version, $"http://127.0.0.1:{_port}/");
        return System.Text.Json.JsonSerializer.Serialize(view, FsperfJson.Default.SettingsView);
    }

    private void HandleSaveSettings(TcpClient c, NetworkStream stream, string origin, string contentType, int length, DateTime deadline)
    {
        byte[] Err(string msg) => Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(msg, FsperfJson.Default.String));
        if (length <= 0 || length > 16 * 1024) {
            Send(stream, 413, "application/json", Err("bad length"));
            c.Client.Shutdown(SocketShutdown.Send);   // no body is read: end politely rather than reset the connection
            return;
        }
        // the body first, whatever the answer: closing a socket with unread data resets the connection, and the client
        // may then never see the answer
        var body = new byte[length];
        for (int got = 0; got < length;) {
            if (DateTime.UtcNow > deadline) return;
            int n = stream.Read(body, got, length - got);
            if (n <= 0) return;
            got += n;
        }
        if (!IsLocal(c) || !OriginIsThisDashboard(origin)) { Send(stream, 403, "application/json", Err("Settings can be changed on the PC running SVS Performance Monitor.")); return; }
        if (!contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) { Send(stream, 415, "application/json", Err("JSON only")); return; }
        if (Settings == null) { Send(stream, 409, "application/json", Err("These settings belong to the tray app. This copy was started from the command line.")); return; }
        AppSettings? s;
        try { s = System.Text.Json.JsonSerializer.Deserialize(body, FsperfJson.Default.AppSettings); } catch { s = null; }
        if (s == null) { Send(stream, 400, "application/json", Err("not settings")); return; }
        string? problem = Settings.Save(s);
        if (problem != null) { Send(stream, 422, "application/json", Err(problem)); return; }
        Send(stream, 200, "application/json", Encoding.UTF8.GetBytes(SettingsViewJson(true)));
        Settings.Saved();   // after the answer is sent: the tray app restarts the monitor (and this server) with them
    }

    // the page's static files (embedded): the design's style sheet and the brand art
    private static readonly Dictionary<string, (string Type, byte[] Body)> Assets = LoadAssets();

    private static Dictionary<string, (string, byte[])> LoadAssets()
    {
        var d = new Dictionary<string, (string, byte[])>(StringComparer.Ordinal);
        foreach (var (route, res, type) in new[] {
            ("/assets/styles.css", "styles.css", "text/css; charset=utf-8"),
            ("/assets/tokens.css", "tokens.css", "text/css; charset=utf-8"),
            ("/assets/logo-64.png", "logo-64.png", "image/png"),
            ("/assets/logo-256.png", "logo-256.png", "image/png"),
            ("/favicon.png", "favicon-32.png", "image/png"),
        }) {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(res);
            if (s == null) continue;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            d[route] = (type, ms.ToArray());
        }
        return d;
    }

    public void Publish(string json)
    {
        _last = json;
        lock (_lock) {
            for (int i = _clients.Count - 1; i >= 0; i--) {
                try { _clients[i].Write($"data: {json}\n\n"); }
                catch {
                    try { _clients[i].Dispose(); } catch { }  // closes the socket too
                    _clients.RemoveAt(i);
                }
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_tail != null) { _tail.Appended -= OnLogAppended; _tail.Heartbeat -= OnLogHeartbeat; }
        try { _listener.Stop(); } catch { }
    }
}
