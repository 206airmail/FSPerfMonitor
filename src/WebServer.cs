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
    private readonly object _lock = new();
    private string _last = "{}";
    private readonly byte[] _page;
    private volatile bool _stop;

    public WebServer(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
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
            var reader = new StreamReader(stream, Encoding.ASCII);
            string? line = reader.ReadLine();
            while (reader.ReadLine() is { Length: > 0 }) { }  // headers
            string path = line?.Split(' ').ElementAtOrDefault(1) ?? "/";
            if (line == null || !line.StartsWith("GET ")) { Send(stream, 405, "text/plain", Encoding.ASCII.GetBytes("GET only")); c.Close(); return; }
            if (path == "/events") {
                var w = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                w.Write("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: keep-alive\r\n\r\n");
                w.Write($"data: {_last}\n\n");
                lock (_lock) _clients.Add(w);
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

    private static void Send(NetworkStream s, int code, string type, byte[] body)
    {
        string reason = code == 200 ? "OK" : code == 404 ? "Not Found" : "Method Not Allowed";
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        s.Write(head);
        s.Write(body);
    }

    public void Publish(string json)
    {
        _last = json;
        lock (_lock) {
            for (int i = _clients.Count - 1; i >= 0; i--) {
                try { _clients[i].Write($"data: {json}\n\n"); } catch { _clients.RemoveAt(i); }
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _listener.Stop(); } catch { }
    }
}
