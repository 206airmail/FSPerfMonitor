// WebServer.cs - the dashboard on http://127.0.0.1:<port>/ (loopback only, no admin rights, no URL reservations):
// GET / = the page, GET /api/snapshot = the last snapshot as JSON, GET /events = server-sent events, one per tick.
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
    private const int MaxEventClients = 16;
    private const int RequestDeadlineMs = 10000;
    private int _reserved;  // live-view slots taken but not yet registered (under _lock)
    private readonly object _lock = new();
    private string _last = "{}";
    private readonly byte[] _page;
    private volatile bool _stop;

    // lan = also reachable from other computers (all interfaces); still no URL reservation (a raw TcpListener), but Windows
    // Firewall must allow the port inbound (Program prints the rule when it is missing)
    public WebServer(int port, bool lan = false)
    {
        _listener = new TcpListener(lan ? IPAddress.Any : IPAddress.Loopback, port);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        _page = ms.ToArray();
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
            while (true) {
                string? h = ReadLineBounded(stream, 8192, deadline);
                if (h == null || ++headers > 100) { c.Close(); return; }
                if (h.Length == 0) break;  // end of the headers
            }
            string path = line.Split(' ').ElementAtOrDefault(1) ?? "/";
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
            if (path == "/api/snapshot") Send(stream, 200, "application/json", Encoding.UTF8.GetBytes(_last));
            else if (path == "/" || path.StartsWith("/?")) Send(stream, 200, "text/html; charset=utf-8", _page);
            else Send(stream, 404, "text/plain", Encoding.ASCII.GetBytes("not found"));
            c.Close();
        } catch {
            try { c.Close(); } catch { }
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
        try { _listener.Stop(); } catch { }
    }
}
