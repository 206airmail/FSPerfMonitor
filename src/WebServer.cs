// WebServer.cs - the dashboard on http://127.0.0.1:<port>/ (loopback only, no admin rights, no URL reservations):
// GET / = the page, GET /api/snapshot = the last snapshot as JSON, GET /events = server-sent events, one per tick.
// 0.3: GET /log = the log page, GET /log/stream = the game's log.txt as server-sent events (see LogTail).
// A minimal HTTP/1.1 server on a TcpListener. Copyright (c) 2026 Jared Brown. MIT licence.
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace FSPerf;

public sealed class WebServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly List<StreamWriter> _clients = new();
    private readonly List<StreamWriter> _logClients = new();   // under _tail.Gate
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
    }

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
            int headers = 0;
            string lastEventId = "";
            while (true) {
                string? h = ReadLineBounded(stream, 8192, deadline);
                if (h == null || ++headers > 100) { c.Close(); return; }
                if (h.Length == 0) break;  // end of the headers
                if (h.StartsWith("Last-Event-ID:", StringComparison.OrdinalIgnoreCase)) lastEventId = h[14..].Trim();
            }
            string path = line.Split(' ').ElementAtOrDefault(1) ?? "/";
            string route = path.Split('?')[0];
            if (!line.StartsWith("GET ")) { Send(stream, 405, "text/plain", Encoding.ASCII.GetBytes("GET only")); c.Close(); return; }
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
    // GET /log/stream?n=500[&after=<epoch>.<seq>]  (or the Last-Event-ID header a reconnecting EventSource sends).
    // Each event: `id: <epoch>.<last seq in it>` and data {"e":epoch,"reset":bool,"gap":bool,"lines":[[seq,text,marker],...]}.
    // reset = the client should drop what it shows (a fresh view); gap = lines between its last id and the first line here were
    // already pushed out of the ring.
    private void ServeLogStream(TcpClient c, NetworkStream stream, string path, string lastEventId)
    {
        var tail = _tail!;
        int n = 500;
        string after = lastEventId;
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
        long afterSeq = -1;
        bool resumed = false;
        int dot = after.IndexOf('.');
        if (dot > 0 && after[..dot] == tail.Epoch && long.TryParse(after[(dot + 1)..], out long a) && a >= 0) { afterSeq = a; resumed = true; }
        try {
            stream.WriteTimeout = 2000;   // a stalled phone must not stall the tailer
            var w = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            lock (tail.Gate) {            // snapshot + register under one lock: no gap, no duplicate between history and live
                if (_logClients.Count >= MaxLogClients) {
                    Send(stream, 503, "text/plain", Encoding.ASCII.GetBytes("too many log views"));
                    c.Close();
                    return;
                }
                if (resumed && afterSeq > tail.LastSeq) { afterSeq = -1; resumed = false; }
                var lines = tail.Snapshot(afterSeq, n, out bool gap);
                w.Write("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nX-Accel-Buffering: no\r\nConnection: keep-alive\r\n\r\nretry: 2000\n\n");
                if (lines.Count > 0 || !resumed) w.Write(LogEvent(tail, lines, !resumed, gap, lines.Count > 0 ? lines[^1].Seq : tail.LastSeq));
                _logClients.Add(w);
            }
        } catch {
            try { c.Close(); } catch { }
        }
    }

    private static string LogEvent(LogTail tail, IReadOnlyList<LogLine> lines, bool reset, bool gap, long lastSeq)
    {
        var sb = new StringBuilder(64 + lines.Count * 120);
        sb.Append("id: ").Append(tail.Epoch).Append('.').Append(lastSeq).Append("\ndata: {\"e\":\"").Append(tail.Epoch).Append("\",\"reset\":")
          .Append(reset ? "true" : "false").Append(",\"gap\":").Append(gap ? "true" : "false").Append(",\"lines\":[");
        for (int i = 0; i < lines.Count; i++) {
            if (i > 0) sb.Append(',');
            sb.Append('[').Append(lines[i].Seq).Append(',').Append(System.Text.Json.JsonSerializer.Serialize(lines[i].Text, LogJson))
              .Append(lines[i].Marker ? ",1]" : ",0]");
        }
        return sb.Append("]}\n\n").ToString();
    }

    private static readonly System.Text.Json.JsonSerializerOptions LogJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // both run under tail.Gate
    private void OnLogAppended(IReadOnlyList<LogLine> lines)
    {
        if (_logClients.Count == 0) return;
        WriteLogClients(LogEvent(_tail!, lines, false, false, lines[^1].Seq));
    }

    private void OnLogHeartbeat() { if (_logClients.Count > 0) WriteLogClients(": keep-alive\n\n"); }

    private void WriteLogClients(string text)
    {
        for (int i = _logClients.Count - 1; i >= 0; i--) {
            try { _logClients[i].Write(text); }
            catch {
                try { _logClients[i].Dispose(); } catch { }
                _logClients.RemoveAt(i);
            }
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

    private static void Send(NetworkStream s, int code, string type, byte[] body)
    {
        string reason = code == 200 ? "OK" : code == 404 ? "Not Found" : code == 503 ? "Service Unavailable" : "Method Not Allowed";
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        s.Write(head);
        s.Write(body);
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
