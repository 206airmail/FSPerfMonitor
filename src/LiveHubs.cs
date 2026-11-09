// LiveHubs.cs - the two live views a browser keeps open: the dashboard's numbers (EventHub, /events) and the game log
// (LogHub, /log/stream). One of each serves this PC's dashboard (WebServer); the relay (#14) has one of each per group
// member who shares. Copyright (c) 2026 Jared Brown. MIT licence.
using System.Net.Sockets;
using System.Text;

namespace FSPerf;

// GET /events: every snapshot as `data: <json>`. A client that stops reading must not stall the publisher: a write that
// takes longer than 2 s throws and that client is dropped.
public sealed class EventHub : IDisposable
{
    private readonly List<StreamWriter> _clients = new();
    private readonly object _lock = new();
    private readonly int _max;
    private int _reserved;   // slots taken but not yet registered (under _lock)
    private volatile string _last = "{}";
    private bool _stop;

    public EventHub(int maxClients) { _max = maxClients; }

    public string Last => _last;
    public int Count { get { lock (_lock) return _clients.Count; } }

    public void Serve(TcpClient c, NetworkStream stream)
    {
        // reserve a slot atomically (check + reserve under one lock), released when registered or failed
        lock (_lock) {
            if (_stop || _clients.Count + _reserved >= _max) {
                WebServer.Send(stream, 503, "text/plain", Encoding.ASCII.GetBytes("too many live views"));
                c.Close();
                return;
            }
            _reserved++;
        }
        bool added = false;
        try {
            stream.WriteTimeout = 2000;
            var w = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            w.Write("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nX-Accel-Buffering: no\r\nConnection: keep-alive\r\n\r\n");
            w.Write($"data: {_last}\n\n");
            lock (_lock) { _reserved--; added = true; if (!_stop) _clients.Add(w); else { try { w.Dispose(); } catch { } } }
        } catch {
            try { c.Close(); } catch { }
        } finally {
            if (!added) lock (_lock) _reserved--;
        }
    }

    // `json` must be one line (it goes into a single `data:` field)
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
        lock (_lock) {
            _stop = true;
            foreach (var w in _clients) { try { w.Dispose(); } catch { } }
            _clients.Clear();
        }
    }
}

// GET /log/stream?n=500[&after=<epoch>.<seq>]  (the Last-Event-ID header a reconnecting EventSource sends wins over ?after=).
// Each event: `id: <epoch>.<last seq in it>` and data {"e":epoch,"reset":bool,"gap":bool,"lines":[[seq,text,marker],...]}.
// reset = the client should drop what it shows (a fresh view); gap = lines between its last id and the first line here were
// already pushed out of the ring.
// Network I/O never happens under tail.Gate: every viewer has a small bounded queue and its own writer thread. A viewer
// whose queue overflows, or whose write exceeds the per-event deadline, is closed (its browser reconnects with Last-Event-ID).
public sealed class LogHub : IDisposable
{
    private readonly LogTail _tail;
    private readonly int _max;
    private readonly List<LogClient> _clients = new();   // under _tail.Gate
    private bool _stop;                                  // under _tail.Gate

    public LogHub(LogTail tail, int maxClients)
    {
        _tail = tail; _max = maxClients;
        tail.Appended += OnAppended;
        tail.Heartbeat += OnHeartbeat;
    }

    public LogTail Tail => _tail;

    private sealed class LogClient
    {
        public const int MaxQueued = 64, EventDeadlineMs = 5000, ChunkBytes = 16 * 1024;
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly Queue<object> _q = new();   // string (ready text) or HistoryItem
        private bool _closed;
        public LogClient(TcpClient tcp, NetworkStream stream) { _tcp = tcp; _stream = stream; }

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

    public void Serve(TcpClient c, NetworkStream stream, string path, string lastEventId)
    {
        var tail = _tail;
        int n = 500;
        string after = "";
        int q = path.IndexOf('?');
        if (q >= 0) {
            foreach (var kv in path[(q + 1)..].Split('&')) {
                int eq = kv.IndexOf('=');
                if (eq < 0) continue;
                string k = kv[..eq], v;
                try { v = Uri.UnescapeDataString(kv[(eq + 1)..]); } catch { continue; }
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
                if (_clients.Count >= _max || _stop) full = true;   // stopping: no new viewers
                else {
                    if (resumed && afterSeq > tail.LastSeq) { afterSeq = -1; resumed = false; }
                    var lines = tail.Snapshot(afterSeq, n, out bool gap);
                    cl.Enqueue("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nX-Accel-Buffering: no\r\nConnection: keep-alive\r\n\r\nretry: 2000\n\n");
                    // the game restarted after this viewer's last line, but the marker already left the ring (Codex #46): the
                    // batch cannot carry the boundary, so tell the page to drop what it shows (reset) instead
                    long mk = tail.LastMarkerSeq;
                    bool lostRestart = resumed && mk > afterSeq && lines.Count > 0 && lines[0].Seq > mk;
                    if (lines.Count > 0 || !resumed) cl.Enqueue(new HistoryItem(lines, !resumed || lostRestart, gap, lines.Count > 0 ? lines[^1].Seq : tail.LastSeq));
                    _clients.Add(cl);
                    client = cl;
                }
            }
            if (full) {
                WebServer.Send(stream, 503, "text/plain", Encoding.ASCII.GetBytes("too many log views"));
                c.Close();
                return;
            }
            new Thread(() => Writer(client!)) { IsBackground = true, Name = "svsperf-logclient" }.Start();
        } catch {
            try { c.Close(); } catch { }
        }
    }

    private void Writer(LogClient cl)
    {
        try {
            while (true) {
                object? item = cl.Next();
                if (item == null) break;
                string text = item is HistoryItem h ? Event(_tail, h.Lines, h.Reset, h.Gap, h.LastSeq) : (string)item;
                if (!cl.Write(text)) break;
            }
        } catch { }
        cl.Close();
        lock (_tail.Gate) _clients.Remove(cl);
    }

    private static string Event(LogTail tail, IReadOnlyList<LogLine> lines, bool reset, bool gap, long lastSeq)
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
    private void OnAppended(IReadOnlyList<LogLine> lines)
    {
        if (_clients.Count == 0) return;
        FanOut(Event(_tail, lines, false, false, lines[^1].Seq));
    }

    private void OnHeartbeat() { if (_clients.Count > 0) FanOut(": keep-alive\n\n"); }

    private void FanOut(string text)
    {
        for (int i = _clients.Count - 1; i >= 0; i--) {
            if (!_clients[i].Enqueue(text)) _clients.RemoveAt(i);   // full queue = dropped
        }
    }

    // GET .../log/download: every line the ring still holds (all game runs in it, restart markers included) as a text
    // file. `name` becomes part of the file name (letters, digits, - and _ only).
    // Written in small pieces (the lines are shared references; only one ~64 KB buffer is extra), within 60 s in all;
    // the callers limit how many run at once.
    public void Download(NetworkStream s, string name)
    {
        List<LogLine> lines;
        lock (_tail.Gate) lines = _tail.Snapshot(0, _tail.RingSize, out _);
        string safe = new string(name.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray()).Trim('_');
        string file = $"{(safe.Length > 0 ? safe : "game")}-log-{DateTime.Now:yyyyMMdd-HHmm}.txt";
        var until = DateTime.UtcNow.AddSeconds(60);
        s.WriteTimeout = 5000;
        // no Content-Length: the body ends when the connection closes (Connection: close)
        s.Write(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Disposition: attachment; filename=\"{file}\"\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n"));
        var sb = new StringBuilder(80 * 1024);
        sb.Append('﻿');   // Notepad and friends read it as UTF-8
        foreach (var l in lines) {
            sb.Append(l.Text).Append("\r\n");
            if (sb.Length < 64 * 1024) continue;
            if (DateTime.UtcNow > until) return;
            s.Write(Encoding.UTF8.GetBytes(sb.ToString()));
            sb.Clear();
        }
        if (sb.Length > 0) s.Write(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    // closes every open log view (its browser reconnects)
    public void Dispose()
    {
        _tail.Appended -= OnAppended;
        _tail.Heartbeat -= OnHeartbeat;
        List<LogClient> all;
        lock (_tail.Gate) { _stop = true; all = [.. _clients]; _clients.Clear(); }
        foreach (var cl in all) cl.Close();   // outside the gate: closing does I/O
    }
}
