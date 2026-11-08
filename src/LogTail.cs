// LogTail.cs - follows the game's log.txt like `tail -f` and keeps the most recent lines in a ring buffer.
// The game rewrites log.txt on every start, and holds it open, so: the file is opened per poll with FileShare.ReadWrite|Delete,
// its identity (volume + file index) tells a recreated file from a grown one, and a length below our offset means "truncated".
// Only the bytes after the saved offset are read. Lines carry a sequence number that never goes back (not even on a restart),
// so a client that reconnects can resume with "everything after seq N". Copyright (c) 2026 Jared Brown. MIT licence.
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FSPerf;

public readonly record struct LogLine(long Seq, string Text, bool Marker);

public sealed class LogTail : IDisposable
{
    public const string RestartText = "=== log restarted (the game started again, log.txt was recreated) ===";
    public const int MaxLineChars = 4000;

    private readonly string _path;
    private readonly LogLine[] _ring;
    private readonly int _pollMs;
    private readonly int _initialBytes;
    private readonly object _gate = new();
    private Timer? _timer;
    private int _busy;

    // read state (only touched by Poll, which is serialised by _busy)
    private long _offset;
    private (uint, uint, uint)? _ident;      // volume serial, file index high, low of the file we follow
    private bool _everSeen;
    private byte[]? _head;                   // the first bytes of the file we follow
    private const int HeadBytes = 256;
    private bool _skipToNewline;             // the first read started mid-file: drop the partial first line
    private byte[] _buf = new byte[64 * 1024];
    private int _len;
    private const int ChunkBytes = 256 * 1024, MaxBytesPerPoll = 4 * 1024 * 1024, MaxPendingBytes = 256 * 1024;

    private long _next = 1;                  // seq of the next line (under _gate)
    private long _lastMarker;                // seq of the newest restart marker, 0 = none (under _gate)
    private int _ticks;

    public string Epoch { get; } = Guid.NewGuid().ToString("N")[..8];   // changes when FSPerf restarts (seq starts over)
    public string Path => _path;

    // raised under Gate for every batch of new lines (and for the keep-alive with an empty list)
    public event Action<IReadOnlyList<LogLine>>? Appended;
    public event Action? Heartbeat;

    public LogTail(string path, int ringSize = 5000, int pollMs = 400, int initialBytes = 1024 * 1024)
    {
        _path = path;
        _ring = new LogLine[Math.Max(16, ringSize)];
        _pollMs = Math.Max(50, pollMs);
        _initialBytes = Math.Max(1024, initialBytes);
    }

    public object Gate => _gate;
    public int RingSize => _ring.Length;

    public void Start() => _timer ??= new Timer(_ => Tick(), null, 0, _pollMs);

    private void Tick()
    {
        try { Poll(); } catch { }
        if (++_ticks % (15000 / _pollMs + 1) == 0) {
            lock (_gate) { try { Heartbeat?.Invoke(); } catch { } }
        }
    }

    public long LastSeq { get { lock (_gate) return _next - 1; } }
    public long LastMarkerSeq { get { lock (_gate) return _lastMarker; } }   // the newest restart marker's seq, 0 = none

    // the lines after seq `after` (after < 0: the newest `n`, but never from before the newest restart marker: a page that
    // opens fresh shows only the current game run, Nick 2026-11-07). gap = the ring no longer holds line after+1. Call under
    // Gate when the result must line up with the live batches.
    public List<LogLine> Snapshot(long after, int n, out bool gap)
    {
        lock (_gate) {
            long last = _next - 1, first = Math.Max(1, _next - _ring.Length);
            gap = false;
            long from;
            if (after < 0) from = Math.Max(Math.Max(first, last - Math.Max(0, n) + 1), _lastMarker);
            else {
                from = after + 1;
                if (from < first) { from = first; gap = true; }
            }
            var list = new List<LogLine>((int)Math.Max(0, last - from + 1));
            for (long s = from; s <= last; s++) list.Add(_ring[s % _ring.Length]);
            return list;
        }
    }

    // one poll: reads what the game appended since the last one. Safe to call directly (tests); never reentrant.
    public bool Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return false;
        try {
            var batch = new List<LogLine>();
            ReadNew(batch);
            if (batch.Count == 0) return false;
            lock (_gate) {
                for (int i = 0; i < batch.Count; i++) {
                    var l = batch[i] with { Seq = _next++ };
                    batch[i] = l;
                    _ring[l.Seq % _ring.Length] = l;
                    if (l.Marker) _lastMarker = l.Seq;
                }
                try { Appended?.Invoke(batch); } catch { }
            }
            return true;
        } finally { Volatile.Write(ref _busy, 0); }
    }

    private void ReadNew(List<LogLine> outLines)
    {
        SafeFileHandle h;
        try { h = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return;   // missing or locked right now; keep the state, the file may come back
        }
        using (h) {
            var id = GetIdent(h);
            long length = RandomAccess.GetLength(h);
            if (_ident == null) {
                if (_everSeen) { Restart(outLines); _offset = 0; }
                else if (length > _initialBytes) { _offset = length - _initialBytes; _skipToNewline = true; }
                else _offset = 0;
                _everSeen = true;
                _ident = id;
            } else if (id != _ident) {                       // a new file at the same path
                Restart(outLines); _offset = 0; _ident = id;
            } else if (length < _offset) {                   // the same file, cut short
                Restart(outLines); _offset = 0;
            }
            // NTFS can hand a recreated file the same identity (and the game may refill it before we look), so the first bytes
            // (the header holds the game's PID and start time) are compared too: a different start means a new run
            if (length > 0) {
                var head = new byte[(int)Math.Min(HeadBytes, length)];
                int hg = RandomAccess.Read(h, head, 0);
                bool restarted = outLines.Count > 0 && outLines[^1].Marker;
                if (hg > 0) {
                    if (_head != null && !restarted) {
                        int c = Math.Min(_head.Length, hg);
                        if (!head.AsSpan(0, c).SequenceEqual(_head.AsSpan(0, c))) { Restart(outLines); _offset = 0; restarted = true; }
                    }
                    if (_head == null || restarted || hg > _head.Length) _head = head.AsSpan(0, hg).ToArray();
                }
            }
            if (length <= _offset) return;                   // nothing new: no read, no work
            var chunk = new byte[ChunkBytes];
            int total = 0;
            while (_offset < length && total < MaxBytesPerPoll) {
                int want = (int)Math.Min(chunk.Length, length - _offset);
                int got = RandomAccess.Read(h, chunk.AsSpan(0, want), _offset);
                if (got <= 0) break;
                _offset += got; total += got;
                Append(chunk, got);
                Extract(outLines);
            }
        }
    }

    private void Restart(List<LogLine> outLines)
    {
        _len = 0; _skipToNewline = false;
        outLines.Add(new LogLine(0, RestartText, true));
    }

    private void Append(byte[] data, int count)
    {
        if (_len + count > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + count));
        Buffer.BlockCopy(data, 0, _buf, _len, count);
        _len += count;
    }

    // splits complete lines out of the pending bytes; a trailing partial line stays pending until its newline arrives
    private void Extract(List<LogLine> outLines)
    {
        int start = 0;
        while (true) {
            int nl = Array.IndexOf(_buf, (byte)'\n', start, _len - start);
            if (nl < 0) break;
            if (_skipToNewline) _skipToNewline = false;      // the partial first line of a mid-file start
            else outLines.Add(new LogLine(0, Decode(_buf, start, nl - start), false));
            start = nl + 1;
        }
        if (start > 0) { _len -= start; if (_len > 0) Buffer.BlockCopy(_buf, start, _buf, 0, _len); }
        if (_len > MaxPendingBytes) {                        // a "line" that never ends: emit it so memory stays bounded
            if (!_skipToNewline) outLines.Add(new LogLine(0, Decode(_buf, 0, _len), false));
            _skipToNewline = false; _len = 0;
        }
    }

    private static string Decode(byte[] b, int off, int n)
    {
        if (n > 0 && b[off + n - 1] == '\r') n--;
        string s = Encoding.UTF8.GetString(b, off, n);
        return s.Length > MaxLineChars ? s[..MaxLineChars] + "... (line cut)" : s;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleInfo
    {
        public uint Attributes; public long Creation, Access, Write; public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleInfo info);

    private static (uint, uint, uint) GetIdent(SafeFileHandle h)
    {
        if (GetFileInformationByHandle(h, out var i)) return (i.VolumeSerial, i.IndexHigh, i.IndexLow);
        return (0, 0, 0);
    }

    public void Dispose() { _timer?.Dispose(); _timer = null; }
}
