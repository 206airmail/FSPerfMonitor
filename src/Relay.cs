// Relay.cs - the group relay (#14): friends' copies send their live numbers and game log here, and every member of the
// group can watch everyone who is sharing. Runs on the host's PC behind a web server that does HTTPS (Caddy on Nick's
// server): it listens on 127.0.0.1 only, and EVERY request on its port counts as coming from the internet (the HTTPS
// front connects from 127.0.0.1 too, so "loopback = this PC" must never be used here). Members, their invite codes and
// removal are managed on the host's own Settings page (the dashboard port, this PC only), never through the relay port.
// Nothing shared is written to disk: the last lines and numbers live in memory and go when sharing stops.
// Copyright (c) 2026 Jared Brown. MIT licence.
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FSPerf;

public sealed class RelayMember
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string CodeHash { get; set; } = "";   // SHA-256 of the normalised code (codes are random, ~119 bits)
    public DateTime Added { get; set; }
}

// relay-members.json next to settings.json: the group. Changes come from the Settings page (one writer: the host's app).
public sealed class RelayMembers
{
    public const int MaxMembers = 50, MaxName = 40;
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";   // no 0/O, 1/I/L: easy to read out and type
    private readonly string _file;
    private readonly object _lock = new();
    private List<RelayMember> _list;
    private DateTime _stamp;   // the file's write time when last read or written

    public RelayMembers(string file) { _file = file; _list = Load(file); _stamp = Stamp(); }

    private DateTime Stamp() { try { return File.GetLastWriteTimeUtc(_file); } catch { return DateTime.MinValue; } }

    // the file changed under us (--group-add / --group-remove while the relay runs): read it again; members that are
    // gone lose their sessions and their share like a removal here. Call under _lock; returns the removed ids.
    private List<string> ReloadLocked()
    {
        var st = Stamp();
        if (st == _stamp) return [];
        var fresh = Load(_file);
        var gone = _list.Where(m => !fresh.Any(f => f.Id == m.Id && f.CodeHash == m.CodeHash)).Select(m => m.Id).ToList();
        _list = fresh; _stamp = st;
        return gone;
    }

    private void Reload()
    {
        List<string> gone;
        lock (_lock) gone = ReloadLocked();
        // on a thread of their own: Reload runs inside callers' locks (the relay's), and a removal takes the relay's locks
        foreach (var id in gone) ThreadPool.QueueUserWorkItem(_ => { try { Removed?.Invoke(id); } catch { } });
    }

    public static string DefaultFile => Path.Combine(Path.GetDirectoryName(SettingsStore.FilePath)!, "relay-members.json");

    public event Action<string>? Removed;   // member id

    private static List<RelayMember> Load(string file)
    {
        try {
            if (File.Exists(file))
                return (JsonSerializer.Deserialize(File.ReadAllText(file), FsperfJson.Default.ListRelayMember) ?? new())
                    .Where(m => m.Id.Length > 0 && m.CodeHash.Length == 64).ToList();
        } catch { }
        return new();
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string tmp = $"{_file}.{Guid.NewGuid():N}.tmp";
        try {
            File.WriteAllText(tmp, JsonSerializer.Serialize(_list, FsperfJson.Default.ListRelayMember));
            File.Move(tmp, _file, overwrite: true);
        } finally { try { File.Delete(tmp); } catch { } }
        _stamp = Stamp();
    }

    public List<RelayMember> All() { Reload(); lock (_lock) return _list.Select(m => new RelayMember { Id = m.Id, Name = m.Name, Added = m.Added }).ToList(); }

    // the new member and their invite code (shown once: only its hash is kept). null + why when not possible.
    public (RelayMember? member, string code, string? problem) Add(string name)
    {
        name = CleanName(name);
        if (name.Length == 0) return (null, "", "Enter a name.");
        Reload();
        lock (_lock) {
            if (_list.Count >= MaxMembers) return (null, "", $"A group can have up to {MaxMembers} members.");
            if (_list.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))) return (null, "", "Someone in the group already has that name.");
            var sb = new StringBuilder();
            for (int i = 0; i < 24; i++) {
                if (i > 0 && i % 4 == 0) sb.Append('-');
                sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
            }
            string code = sb.ToString();
            var m = new RelayMember { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(), Name = name, CodeHash = Hash(code), Added = DateTime.UtcNow };
            _list.Add(m);
            try { SaveLocked(); } catch (Exception e) { _list.Remove(m); return (null, "", "Could not save the group: " + e.Message); }
            return (m, code, null);
        }
    }

    public bool Remove(string id)
    {
        Reload();
        lock (_lock) {
            int i = _list.FindIndex(m => m.Id == id);
            if (i < 0) return false;
            var m = _list[i];
            _list.RemoveAt(i);
            try { SaveLocked(); } catch { _list.Insert(i, m); return false; }
        }
        ThreadPool.QueueUserWorkItem(_ => { try { Removed?.Invoke(id); } catch { } });
        return true;
    }

    // the member with this invite code, or null
    public RelayMember? Find(string code)
    {
        string h = Hash(code);
        if (h.Length == 0) return null;
        Reload();
        lock (_lock) {
            foreach (var m in _list)
                if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(m.CodeHash), Encoding.ASCII.GetBytes(h))) return m;
        }
        return null;
    }

    // HMAC-SHA256 with the group's secret key (relay-key.bin next to the members file, made on first use)
    public string Sign(string data)
    {
        lock (_lock) {
            if (_key == null) {
                string kf = Path.Combine(Path.GetDirectoryName(_file)!, "relay-key.bin");
                try { if (File.Exists(kf)) { var k = File.ReadAllBytes(kf); if (k.Length == 32) _key = k; } } catch { }
                if (_key == null) {
                    _key = RandomNumberGenerator.GetBytes(32);
                    try { Directory.CreateDirectory(Path.GetDirectoryName(kf)!); File.WriteAllBytes(kf, _key); } catch { }   // unsaved: sign-ins last until a restart
                }
            }
            return Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(data)));
        }
    }
    private byte[]? _key;

    public RelayMember? Get(string id) { Reload(); lock (_lock) return _list.FirstOrDefault(m => m.Id == id); }

    // letters and digits only, upper case (the dashes and spaces people type or copy along are ignored)
    public static string Normalize(string code) => new(code.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string Hash(string code)
    {
        string n = Normalize(code);
        if (n.Length != 24) return "";
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(n)));
    }

    public static string CleanName(string? name)
    {
        string s = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return s.Length > MaxName ? s[..MaxName].Trim() : s;
    }
}

public sealed class RelayServer : IDisposable
{
    public const int MaxBody = 512 * 1024, MaxLinesPerPush = 1000, RingLines = 10000, PluginRingLines = 3000;
    // characters kept per log (a line is up to 4,000): ~6 MB for a member's game log, ~2 MB for each other log
    public const long GameChars = 3_000_000, OtherChars = 1_000_000;
    private const int RequestDeadlineMs = 10000, SessionDays = 30;
    // everything on this port comes from the internet: bounded work at every step (Codex #14)
    private const int MaxActive = 64, MaxLiveViews = 128, MaxSmallBody = 4 * 1024;
    // #16 full logs on request: one zip per member, 16 MB at most, 64 MB for the whole group, kept an hour; a request
    // waits 30 minutes for the sharer's answer; one request per member a minute
    public const int MaxFullLogs = 16 * 1024 * 1024;
    private const long MaxFullLogsTotal = 64L * 1024 * 1024;
    private static readonly TimeSpan FullLogsKept = TimeSpan.FromHours(1), RequestWaits = TimeSpan.FromMinutes(30);
    private const string Cookie = "svs_group";
    private static readonly TimeSpan Away = TimeSpan.FromSeconds(30), Gone = TimeSpan.FromMinutes(10);

    private readonly TcpListener _listener;
    private readonly RelayMembers _members;
    private readonly object _lock = new();
    private readonly Dictionary<string, Share> _shares = new();                       // member id -> what they share (under _lock)
    private readonly Dictionary<string, (int Count, DateTime Since)> _failures = new();         // client address -> failed sign-ins
    private readonly Dictionary<string, List<TcpClient>> _views = new();   // viewer member id -> their open live views (under _lock)
    // member id -> the sharing runs that stopped (the newest 8; under _lock). A stopped run never comes back, whatever
    // their other runs (another PC with the same code, a restart) send meanwhile
    private readonly Dictionary<string, List<string>> _stopped = new();
    private readonly SemaphoreSlim _downloads = new(2, 2);
    private int _active;                                                    // requests being handled right now
    private readonly Timer _sweep;
    private readonly byte[] _groupPage, _joinPage, _dashPage, _logPage;
    private volatile bool _stop;

    public int Port { get; }

    private sealed class Share : IDisposable
    {
        public readonly string MemberId;
        public string Name;
        public readonly EventHub Events = new(8);
        public readonly Dictionary<string, Source> Logs = new();   // log id ("game", "fsp", "plugin:<Name>"...) -> its lines (under the share's lock)
        public string Run = "";        // the sharer's current sharing run (a stop names it)
        // #16 (under the relay's _lock): the open request for their full logs, and the zip they sent
        public string ReqId = "", ReqBy = "";
        public DateTime ReqAt;
        public bool Declined;
        public byte[]? Full;
        public DateTime FullAt;
        public string FullBy = "";
        public int Port;               // their PC's dashboard port: "Settings on this PC" for this member's own browsers
        public DateTime Since = DateTime.UtcNow, LastPush = DateTime.MinValue;   // LastPush: the last ACCEPTED push
        public Share(string id, string name) { MemberId = id; Name = name; }

        public sealed class Source
        {
            public readonly LogHub Hub;
            public string Epoch = "";  // the sharer's tail run for this log: a new one = their app restarted
            public long LastSeq;       // the newest line of theirs we have (their numbering)
            public Source(bool game) { Hub = new LogHub(LogTail.Detached(game ? RingLines : PluginRingLines, game ? GameChars : OtherChars), 8); }
        }

        public Source? Get(string id, bool create)
        {
            lock (this) {
                if (Logs.TryGetValue(id, out var src)) return src;
                if (!create || Logs.Count >= LogSources.Max) return null;
                return Logs[id] = new Source(id == LogSources.Game);
            }
        }

        // logs the sharer no longer has (a plugin removed, another set after a restart): their slots come free
        public void Keep(HashSet<string> ids)
        {
            List<Source> gone;
            lock (this) {
                gone = Logs.Where(kv => !ids.Contains(kv.Key) && kv.Key != LogSources.Game).Select(kv => kv.Value).ToList();
                foreach (var k in Logs.Keys.Where(k => !ids.Contains(k) && k != LogSources.Game).ToList()) Logs.Remove(k);
            }
            foreach (var g in gone) g.Hub.Dispose();
        }

        public List<string> Ids() { lock (this) return Logs.Keys.OrderBy(k => k == LogSources.Game ? 0 : k == "fsp" ? 1 : k == "loader" ? 2 : 3).ThenBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(); }
        public List<LogTail> Tails() { lock (this) return Logs.Values.Select(v => v.Hub.Tail).ToList(); }

        public void Dispose()
        {
            Events.Dispose();
            List<Source> all;
            lock (this) { all = Logs.Values.ToList(); Logs.Clear(); }
            foreach (var x in all) x.Hub.Dispose();
        }
    }

    public RelayServer(int port, RelayMembers members)
    {
        Port = port;
        _members = members;
        _members.Removed += OnMemberRemoved;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _groupPage = Resource("group.html");
        _joinPage = Resource("join.html");
        _dashPage = Resource("index.html");
        _logPage = Resource("log.html");
        _sweep = new Timer(_ => Sweep(), null, 15000, 15000);
    }

    private static byte[] Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public void Start()
    {
        _listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "svsperf-relay" }.Start();
    }

    private void AcceptLoop()
    {
        while (!_stop) {
            TcpClient c;
            try { c = _listener.AcceptTcpClient(); } catch { return; }
            if (Interlocked.Increment(ref _active) > MaxActive) {   // busy: drop rather than queue without end
                Interlocked.Decrement(ref _active);
                try { c.Close(); } catch { }
                continue;
            }
            ThreadPool.QueueUserWorkItem(_ => { try { Handle(c); } finally { Interlocked.Decrement(ref _active); } });
        }
    }

    // every 15 s: keep-alive for open log views, mark the quiet, forget sharers gone for 10 minutes and old sessions
    private void Sweep()
    {
        List<Share> drop = new(), live;
        lock (_lock) {
            var now = DateTime.UtcNow;
            foreach (var (id, s) in _shares.ToList())
                if (now - s.LastPush > Gone) { _shares.Remove(id); drop.Add(s); }
            foreach (var s in _shares.Values) {   // #16: requests and full logs expire
                if (s.ReqId.Length > 0 && now - s.ReqAt > RequestWaits) s.ReqId = "";
                if (s.Full != null && now - s.FullAt > FullLogsKept) s.Full = null;
            }
            live = _shares.Values.ToList();
            foreach (var (ip, f) in _failures.ToList()) if (now - f.Since > TimeSpan.FromMinutes(10)) _failures.Remove(ip);
            PruneViewsLocked();
        }
        foreach (var s in drop) s.Dispose();
        foreach (var s in live) foreach (var t in s.Tails()) t.Beat();
    }

    // a removed member: their share goes (its viewers are closed), and so does everything they are watching; their
    // sign-in no longer checks out (Viewer)
    private void OnMemberRemoved(string id)
    {
        Share? s;
        List<TcpClient>? views;
        lock (_lock) { _shares.Remove(id, out s); _views.Remove(id, out views); _stopped.Remove(id); }
        s?.Dispose();
        foreach (var c in views ?? []) { try { c.Close(); } catch { } }
    }

    private static bool Open(TcpClient c) { try { return c.Client?.Connected == true; } catch { return false; } }

    private void PruneViewsLocked()
    {
        foreach (var (id, list) in _views.ToList()) {
            list.RemoveAll(c => !Open(c));
            if (list.Count == 0) _views.Remove(id);
        }
    }

    // a live view for `viewer`: false = too many open (all viewers together)
    private bool AddView(string viewer, TcpClient c)
    {
        lock (_lock) {
            int n = _views.Values.Sum(l => l.Count);
            if (n >= MaxLiveViews) { PruneViewsLocked(); n = _views.Values.Sum(l => l.Count); }
            if (n >= MaxLiveViews) return false;
            if (!_views.TryGetValue(viewer, out var list)) _views[viewer] = list = new();
            list.Add(c);
            return true;
        }
    }

    // ---- requests ---------------------------------------------------------------------------------------------------

    private sealed class Request
    {
        public string Method = "", Path = "", Route = "", Host = "", Origin = "", ContentType = "", Auth = "", Cookie = "", LastEventId = "", Client = "", RequestId = "";
        public int Length;
    }

    private void Handle(TcpClient c)
    {
        try {
            var stream = c.GetStream();
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 5000;
            var deadline = DateTime.UtcNow.AddMilliseconds(RequestDeadlineMs);
            string? line = WebServer.ReadLineBounded(stream, 2048, deadline);
            if (line == null) { c.Close(); return; }
            var r = new Request();
            var parts = line.Split(' ');
            if (parts.Length != 3) { c.Close(); return; }
            r.Method = parts[0]; r.Path = parts[1]; r.Route = r.Path.Split('?')[0];
            int headers = 0;
            while (true) {
                string? h = WebServer.ReadLineBounded(stream, 8192, deadline);
                if (h == null || ++headers > 100) { c.Close(); return; }
                if (h.Length == 0) break;
                int colon = h.IndexOf(':');
                if (colon <= 0) continue;
                string k = h[..colon].Trim(), v = h[(colon + 1)..].Trim();
                switch (k.ToLowerInvariant()) {
                    case "host": r.Host = v; break;
                    case "origin": r.Origin = v; break;
                    case "content-type": r.ContentType = v; break;
                    case "authorization": r.Auth = v; break;
                    case "cookie": r.Cookie = r.Cookie.Length > 0 ? r.Cookie + "; " + v : v; break;
                    case "last-event-id": r.LastEventId = v; break;
                    case "x-svs-request": r.RequestId = v.Length <= 32 ? v : ""; break;
                    case "x-forwarded-for": r.Client = v.Split(',')[0].Trim(); break;   // set by the HTTPS front (Caddy)
                    case "content-length": int.TryParse(v, out r.Length); break;
                }
            }
            if (r.Client.Length == 0) r.Client = (c.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
            if (r.Method == "POST") { HandlePost(c, stream, r, deadline); c.Close(); return; }
            if (r.Method != "GET") { Send(stream, 405, "text/plain", "GET or POST only"); c.Close(); return; }
            if (HandleGet(c, stream, r)) return;   // a live view: kept open
            c.Close();
        } catch {
            try { c.Close(); } catch { }
        }
    }

    // true = the connection stays open (a live view)
    private bool HandleGet(TcpClient c, NetworkStream stream, Request r)
    {
        if (WebServer.Assets.TryGetValue(r.Route, out var asset)) { Send(stream, 200, asset.Type, asset.Body, cache: true); return false; }
        if (r.Route == "/") { Send(stream, 200, "text/html; charset=utf-8", _groupPage); return false; }
        // #18: the invite link's page (the code stays in its fragment), and the app's "who does this code belong to?"
        if (r.Route == "/join") { Send(stream, 200, "text/html; charset=utf-8", _joinPage); return false; }
        if (r.Route == "/api/whoami") {
            var who = Sender(r);
            if (who == null) { Send(stream, Failed(r.Client) ? 429 : 401, "application/json", "\"This invite code is not in the group.\""); return false; }
            Send(stream, 200, "application/json", $"{{\"name\":{JsonSerializer.Serialize(who.Name, FsperfJson.Default.String)}}}");
            return false;
        }
        // a new HTTPS name is found by crawlers within minutes (certificate logs): ask them to stay out
        if (r.Route == "/robots.txt") { Send(stream, 200, "text/plain", "User-agent: *\nDisallow: /\n", cache: true); return false; }
        var me = Viewer(r);
        if (r.Route == "/api/group") {
            if (me == null) { Send(stream, 401, "application/json", "\"Sign in with your invite code.\""); return false; }
            Send(stream, 200, "application/json", GroupJson(me));
            return false;
        }
        // /s/<id>/, /s/<id>/log, /s/<id>/events, /s/<id>/log/stream
        if (r.Route.StartsWith("/s/", StringComparison.Ordinal)) {
            string rest = r.Route[3..];
            int slash = rest.IndexOf('/');
            if (slash <= 0) { Redirect(stream, r.Route + "/"); return false; }
            string id = rest[..slash], sub = rest[(slash + 1)..];
            if (me == null) { if (sub.Length == 0 || sub == "log") Redirect(stream, "/"); else Send(stream, 401, "text/plain", "sign in"); return false; }
            Share? s;
            lock (_lock) _shares.TryGetValue(id, out s);
            if (s == null) {
                if (sub.Length == 0 || sub == "log") Redirect(stream, "/?gone=1");
                else Send(stream, 404, "text/plain", "not sharing");
                return false;
            }
            switch (sub) {
                case "": Send(stream, 200, "text/html; charset=utf-8", MemberPage(_dashPage, s.Name, MyPort(me))); return false;
                case "log": Send(stream, 200, "text/html; charset=utf-8", MemberPage(_logPage, s.Name, MyPort(me))); return false;
                case "api/full-logs": {   // #16: {"pending","by","declined","ready","at","size","from"}
                    string json;
                    lock (_lock) {
                        string S(string v) => JsonSerializer.Serialize(v, FsperfJson.Default.String);
                        json = $"{{\"pending\":{(s.ReqId.Length > 0 ? "true" : "false")},\"by\":{S(s.ReqBy)},\"declined\":{(s.Declined ? "true" : "false")}," +
                               $"\"ready\":{(s.Full != null ? "true" : "false")},\"at\":{JsonSerializer.Serialize(s.FullAt, FsperfJson.Default.DateTime)}," +
                               $"\"size\":{s.Full?.Length ?? 0},\"from\":{S(s.FullBy)}}}";
                    }
                    Send(stream, 200, "application/json", json);
                    return false;
                }
                case "full-logs.zip": {
                    byte[]? zip; DateTime at;
                    lock (_lock) { zip = s.Full; at = s.FullAt; }
                    if (zip == null) { Send(stream, 404, "text/plain", "no full logs"); return false; }
                    string safe = new string(s.Name.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray()).Trim('_');
                    stream.WriteTimeout = 30000;
                    stream.Write(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: {zip.Length}\r\n" +
                        $"Content-Disposition: attachment; filename=\"{(safe.Length > 0 ? safe : "member")}-full-logs-{at.ToLocalTime():yyyyMMdd-HHmm}.zip\"\r\n" +
                        "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Robots-Tag: noindex, nofollow\r\nConnection: close\r\n\r\n"));
                    stream.Write(zip);
                    return false;
                }
                case "api/logs": {   // #15: the logs this member shares, the game's first
                    var ids = s.Ids();
                    if (!ids.Contains(LogSources.Game)) ids.Insert(0, LogSources.Game);
                    Send(stream, 200, "application/json", WebServer.LogListJson(ids.Select(i => new LogSource(i, LogSources.LabelFor(i), ""))));
                    return false;
                }
                case "events":
                    if (!AddView(me.Id, c)) { Send(stream, 503, "text/plain", "too many live views"); return false; }
                    s.Events.Serve(c, stream);
                    return true;
                case "log/stream": {
                    var src = s.Get(WebServer.SourceOf(r.Path), create: false);
                    if (src == null) { Send(stream, 404, "text/plain", "not shared"); return false; }
                    if (!AddView(me.Id, c)) { Send(stream, 503, "text/plain", "too many live views"); return false; }
                    src.Hub.Serve(c, stream, r.Path, r.LastEventId);
                    return true;
                }
                case "log/download": {
                    string lid = WebServer.SourceOf(r.Path);
                    var src = s.Get(lid, create: false);
                    if (src == null) { Send(stream, 404, "text/plain", "not shared"); return false; }
                    if (!_downloads.Wait(0)) { Send(stream, 503, "text/plain", "busy, try again in a moment"); return false; }
                    try { src.Hub.Download(stream, lid == LogSources.Game ? s.Name : s.Name + "-" + LogSources.LabelFor(lid)); } finally { _downloads.Release(); }
                    return false;
                }
            }
        }
        Send(stream, 404, "text/plain", "not found");
        return false;
    }

    private void HandlePost(TcpClient c, NetworkStream stream, Request r, DateTime deadline)
    {
        void Refuse(int code, string msg) { Send(stream, code, "application/json", msg); try { c.Client.Shutdown(SocketShutdown.Send); } catch { } }
        bool sharing = r.Route is "/api/push" or "/api/stop" or "/api/full-logs/send" or "/api/full-logs/decline";
        int limit = r.Route == "/api/push" ? MaxBody : r.Route == "/api/full-logs/send" ? MaxFullLogs : MaxSmallBody;
        if (r.Length < 0 || r.Length > limit) { Refuse(413, "\"too big\""); return; }
        if (r.Route == "/api/full-logs/send") deadline = DateTime.UtcNow.AddMinutes(3);   // 16 MB on a slow upload
        // a sender proves the invite code before anything is read or kept for it
        RelayMember? sender = null;
        if (sharing) {
            sender = Sender(r);
            if (sender == null) {
                Refuse(Failed(r.Client) ? 429 : 401, "\"This invite code is not in the group.\"");
                return;
            }
        }
        // the body first, whatever the answer (closing with unread data resets the connection)
        var body = new byte[r.Length];
        for (int got = 0; got < r.Length;) {
            if (DateTime.UtcNow > deadline) return;
            int n = stream.Read(body, got, r.Length - got);
            if (n <= 0) return;
            got += n;
        }
        if (r.Route == "/api/full-logs/send") { ReceiveFullLogs(stream, sender!, r, body); return; }
        if (!r.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) { Send(stream, 415, "application/json", "\"JSON only\""); return; }
        switch (r.Route) {
            case "/api/full-logs/ask": AskFullLogs(stream, r, body); return;
            case "/api/full-logs/decline": {
                lock (_lock) if (_shares.TryGetValue(sender!.Id, out var ds) && ds.ReqId.Length > 0) { ds.ReqId = ""; ds.Declined = true; }
                Send(stream, 200, "application/json", "{}");
                return;
            }
            case "/api/push": Push(stream, sender!, body); return;
            case "/api/stop": {
                // {"run":"<the sharing run that stops>"}: a push of that run arriving late is refused (it must not bring
                // the share back); a new run (sharing turned on again, the app started again) shares normally
                string run = "";
                try { using var d = JsonDocument.Parse(body); if (d.RootElement.TryGetProperty("run", out var e) && e.ValueKind == JsonValueKind.String) run = e.GetString() ?? ""; } catch { }
                Share? s;
                lock (_lock) { _shares.Remove(sender!.Id, out s); if (run.Length is > 0 and <= 32) {
                    if (!_stopped.TryGetValue(sender.Id, out var runs)) _stopped[sender.Id] = runs = new();
                    if (!runs.Contains(run)) { runs.Add(run); if (runs.Count > 8) runs.RemoveAt(0); }
                } }
                s?.Dispose();
                Send(stream, 200, "application/json", "{}");
                return;
            }
            case "/api/signin": SignIn(stream, r, body); return;
            case "/api/signout": {
                if (!SameOrigin(r)) { Send(stream, 403, "application/json", "\"forbidden\""); return; }
                Send(stream, 200, "application/json", "{}", setCookie: $"{Cookie}=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Strict");
                return;
            }
        }
        Send(stream, 404, "application/json", "\"not found\"");
    }

    // ---- sharing: POST /api/push --------------------------------------------------------------------------------------
    // Authorization: Bearer <invite code>. Body (#15):
    //   {"run":"<this sharing run>","port":<their dashboard port>,"snapshot":{...}|null,
    //    "logs":[{"id":"game"|"fsp"|"loader"|"plugin:<Name>","epoch":"<that tail's run>","after":<n>,"lines":[[seq,"text",0|1],...]}]}
    // "after": the newest line of that log they believe we have (-1 = none: they send its recent history).
    // Answer: {"name":"<their name>","acks":{"<id>":<their newest line we have>},"resync":["<id>",...]}: a log in "resync"
    // is one we don't know (we restarted, or forgot them after a quiet spell) although they think we do: they send its
    // recent history again, so nothing they still have is skipped.
    // 409 "stopped": that sharing run stopped; a late push of it is ignored.
    private const int MaxLinesInPush = 4000;

    private RelayMember? Sender(Request r) =>
        r.Auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? _members.Find(r.Auth[7..]) : null;

    private sealed record PushLog(string Id, string Epoch, long After, List<(long Seq, string Text, bool Marker)> Lines);

    private void Push(NetworkStream stream, RelayMember m, byte[] body)
    {
        string run;
        int port = 0;
        string? snapshot = null;
        var logs = new List<PushLog>();
        HashSet<string>? all = null;   // every log the sharer has (sends may carry only some of them)
        try {
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            run = root.TryGetProperty("run", out var rn) && rn.ValueKind == JsonValueKind.String ? rn.GetString()! : "";
            if (run.Length is 0 or > 32) throw new FormatException();
            if (root.TryGetProperty("port", out var pt) && pt.TryGetInt32(out int pv) && pv is >= 1 and <= 65535) port = pv;
            if (root.TryGetProperty("snapshot", out var sn) && sn.ValueKind == JsonValueKind.Object) {
                // only what the dashboard knows, typed: a number stays a number, a text stays text (the pages escape text)
                try {
                    var snap = sn.Deserialize(FsperfJson.Default.Snapshot);
                    if (snap != null) snapshot = JsonSerializer.Serialize(snap, FsperfJson.Default.Snapshot);
                } catch { }
            }
            if (root.TryGetProperty("all", out var al) && al.ValueKind == JsonValueKind.Array)
                all = al.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
                        .Where(LogSources.ValidId).Take(LogSources.Max).ToHashSet();
            int total = 0;
            if (root.TryGetProperty("logs", out var ls) && ls.ValueKind == JsonValueKind.Array) {
                foreach (var lg in ls.EnumerateArray()) {
                    if (logs.Count >= LogSources.Max) break;
                    if (lg.ValueKind != JsonValueKind.Object) continue;
                    string id = lg.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
                    string epoch = lg.TryGetProperty("epoch", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
                    if (!LogSources.ValidId(id) || epoch.Length is 0 or > 32 || logs.Any(x => x.Id == id)) continue;
                    long after = lg.TryGetProperty("after", out var af) && af.TryGetInt64(out long a) ? a : -1;
                    var lines = new List<(long, string, bool)>();
                    if (lg.TryGetProperty("lines", out var ln) && ln.ValueKind == JsonValueKind.Array) {
                        foreach (var l in ln.EnumerateArray()) {
                            if (lines.Count >= MaxLinesPerPush || total >= MaxLinesInPush) break;
                            if (l.ValueKind != JsonValueKind.Array || l.GetArrayLength() < 2) continue;
                            if (!l[0].TryGetInt64(out long seq) || l[1].ValueKind != JsonValueKind.String) continue;
                            string text = l[1].GetString() ?? "";
                            if (text.Length > LogTail.MaxLineChars) text = text[..LogTail.MaxLineChars];
                            bool marker = l.GetArrayLength() > 2 && l[2].ValueKind == JsonValueKind.Number && l[2].TryGetInt32(out int mk) && mk == 1;
                            lines.Add((seq, text, marker));
                            total++;
                        }
                    }
                    logs.Add(new PushLog(id, epoch, after, lines));
                }
            }
        } catch {
            Send(stream, 400, "application/json", "\"bad push\"");
            return;
        }
        Share? share;
        string refuse = "";
        lock (_lock) {
            // still a member with this code? (removal may have happened since the code was checked: a removed member's
            // share must not come back; the removal handler takes this same lock after updating the list)
            var cur = _members.Get(m.Id);
            if (cur == null || cur.CodeHash != m.CodeHash) refuse = "\"This invite code is not in the group.\"";
            else if (_stopped.TryGetValue(m.Id, out var stoppedRuns) && stoppedRuns.Contains(run)) refuse = "\"stopped\"";
            else {
                if (!_shares.TryGetValue(m.Id, out share)) _shares[m.Id] = share = new Share(m.Id, m.Name);
                else if (DateTime.UtcNow - share.LastPush < TimeSpan.FromMilliseconds(500)) refuse = "\"Too fast.\"";
            }
            share = refuse.Length == 0 ? _shares[m.Id] : null;
        }
        if (share == null) { Send(stream, refuse.Contains("Too fast") ? 429 : refuse.Contains("stopped") ? 409 : 401, "application/json", refuse); return; }
        var acks = new StringBuilder();
        var resync = new List<string>();
        lock (share) {   // one push per member at a time: the epoch / LastSeq bookkeeping and the order of the lines
            lock (_lock) { share.Name = m.Name; share.LastPush = DateTime.UtcNow; share.Port = port; share.Run = run; }
            if (all != null) share.Keep(all);
            foreach (var lg in logs) {
                var src = share.Get(lg.Id, create: lg.After < 0);
                if (src == null || (src.Epoch != lg.Epoch && lg.After >= 0)) {
                    if (src != null || lg.After >= 0) resync.Add(lg.Id);   // we don't have what they think we have
                    continue;
                }
                var batch = new List<LogLine>();
                if (src.Epoch != lg.Epoch) {
                    // their app restarted (its numbering starts over): a marker, so a fresh view starts at their new run
                    if (src.Epoch.Length > 0) batch.Add(new LogLine(0, lg.Id == LogSources.Game ? $"=== {m.Name} started sharing again ===" : $"=== {LogSources.LabelFor(lg.Id)}: {m.Name} started sharing again ===", true));
                    src.Epoch = lg.Epoch;
                    src.LastSeq = 0;
                }
                long last = src.LastSeq;
                foreach (var (seq, text, marker) in lg.Lines) {
                    if (seq <= last) continue;   // already have it (a retried push)
                    batch.Add(new LogLine(0, text, marker));
                    last = seq;
                }
                src.LastSeq = last;
                src.Hub.Tail.Append(batch);
                acks.Append(acks.Length > 0 ? "," : "").Append(JsonSerializer.Serialize(lg.Id, FsperfJson.Default.String)).Append(':').Append(last);
            }
            if (snapshot != null) share.Events.Publish(snapshot);
        }
        string rs = string.Join(",", resync.Select(x => JsonSerializer.Serialize(x, FsperfJson.Default.String)));
        string req = "";
        lock (_lock) if (share.ReqId.Length > 0) req = $",\"request\":{{\"id\":\"{share.ReqId}\",\"by\":{JsonSerializer.Serialize(share.ReqBy, FsperfJson.Default.String)}}}";
        Send(stream, 200, "application/json", $"{{\"name\":{JsonSerializer.Serialize(m.Name, FsperfJson.Default.String)},\"acks\":{{{acks}}},\"resync\":[{rs}]{req}}}");
    }

    // ---- #16 full logs on request -------------------------------------------------------------------------------------
    // A member asks (POST /api/full-logs/ask {"id":"<member id>"}, signed in, same site); the sharer's next push answer
    // carries {"request":{"id","by"}}; their app asks THEM, then sends a zip (POST /api/full-logs/send, Bearer, header
    // X-SVS-Request: <request id>, application/zip) or declines. Any member can then download it for an hour.
    private void AskFullLogs(NetworkStream stream, Request r, byte[] body)
    {
        if (!SameOrigin(r)) { Send(stream, 403, "application/json", "\"forbidden\""); return; }
        var me = Viewer(r);
        if (me == null) { Send(stream, 401, "application/json", "\"Sign in with your invite code.\""); return; }
        string id = "";
        try { using var d = JsonDocument.Parse(body); if (d.RootElement.TryGetProperty("id", out var e) && e.ValueKind == JsonValueKind.String) id = e.GetString() ?? ""; } catch { }
        string answer;
        lock (_lock) {
            if (!_shares.TryGetValue(id, out var s)) answer = "\"They are not sharing right now.\"";
            else if (s.ReqId.Length > 0) answer = "";   // already asked: still waiting for them
            else if (DateTime.UtcNow - s.ReqAt < TimeSpan.FromMinutes(1)) answer = "\"Asked a moment ago. Try again in a minute.\"";
            else { s.ReqId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)); s.ReqBy = me.Name; s.ReqAt = DateTime.UtcNow; s.Declined = false; answer = ""; }
        }
        if (answer.Length > 0) Send(stream, answer.Contains("not sharing") ? 404 : 429, "application/json", answer);
        else Send(stream, 200, "application/json", "{}");
    }

    private void ReceiveFullLogs(NetworkStream stream, RelayMember m, Request r, byte[] body)
    {
        if (!r.ContentType.StartsWith("application/zip", StringComparison.OrdinalIgnoreCase) || body.Length < 22 || body[0] != 'P' || body[1] != 'K') {
            Send(stream, 415, "application/json", "\"zip only\""); return;
        }
        string answer = "";
        lock (_lock) {
            if (!_shares.TryGetValue(m.Id, out var s) || s.ReqId.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s.ReqId), Encoding.ASCII.GetBytes(r.RequestId)))
                answer = "\"Nobody asked for them (or the request expired).\"";
            else {
                long others = _shares.Values.Where(x => x != s && x.Full != null).Sum(x => (long)x.Full!.Length);
                if (others + body.Length > MaxFullLogsTotal) answer = "\"The group has no room for more full logs right now. Try again later.\"";
                else { s.Full = body; s.FullAt = DateTime.UtcNow; s.FullBy = s.ReqBy; s.ReqId = ""; s.Declined = false; }
            }
        }
        if (answer.Length > 0) Send(stream, answer.Contains("room") ? 507 : 409, "application/json", answer);
        else Send(stream, 200, "application/json", "{}");
    }

    // ---- viewers: sign in with the invite code, then a session cookie -------------------------------------------------

    private void SignIn(NetworkStream stream, Request r, byte[] body)
    {
        if (!SameOrigin(r)) { Send(stream, 403, "application/json", "\"forbidden\""); return; }
        lock (_lock) {
            if (_failures.TryGetValue(r.Client, out var f) && f.Count >= 10) { Send(stream, 429, "application/json", "\"Too many tries. Wait a few minutes.\""); return; }
        }
        string code = "";
        try {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString() ?? "";
        } catch { }
        var m = _members.Find(code);
        if (m == null) {
            Failed(r.Client);
            Send(stream, 401, "application/json", "\"That invite code is not in this group.\"");
            return;
        }
        long exp = DateTimeOffset.UtcNow.AddDays(SessionDays).ToUnixTimeSeconds();
        string token = $"{m.Id}.{exp}.{_members.Sign($"{m.Id}.{exp}.{m.CodeHash}")}";
        Send(stream, 200, "application/json", GroupJson(m),
            setCookie: $"{Cookie}={token}; Path=/; Max-Age={SessionDays * 86400}; HttpOnly; Secure; SameSite=Strict");
    }

    // true = this address has failed too often (counts this failure)
    private bool Failed(string client)
    {
        lock (_lock) {
            var now = DateTime.UtcNow;
            (int Count, DateTime Since) f = _failures.TryGetValue(client, out var v) && now - v.Since < TimeSpan.FromMinutes(10) ? v : (0, now);
            f.Count++;
            _failures[client] = f;
            if (_failures.Count > 10000) _failures.Clear();   // a flood of addresses: start over rather than grow
            return f.Count > 10;
        }
    }

    // a browser's POST must come from this site: the Origin's host is the Host the request was sent to
    private static bool SameOrigin(Request r)
    {
        if (r.Origin.Length == 0 || r.Host.Length == 0) return false;
        return Uri.TryCreate(r.Origin, UriKind.Absolute, out var o) && string.Equals(o.Authority, r.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static string CookieToken(Request r)
    {
        foreach (var part in r.Cookie.Split(';')) {
            var kv = part.Trim();
            if (kv.StartsWith(Cookie + "=", StringComparison.Ordinal)) return kv[(Cookie.Length + 1)..];
        }
        return "";
    }

    // the sign-in cookie: "<member id>.<expiry, unix s>.<HMAC of both and the member's code hash>" with the group's own
    // key (relay-key.bin). Signed rather than remembered, so a restart of the relay (e.g. a settings save on the host)
    // signs nobody out; a member who is removed (or gets a new code) no longer checks out at once.
    private RelayMember? Viewer(Request r)
    {
        var parts = CookieToken(r).Split('.');
        if (parts.Length != 3 || !long.TryParse(parts[1], out long exp) || exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
        var m = _members.Get(parts[0]);
        if (m == null) return null;
        string want = _members.Sign($"{m.Id}.{exp}.{m.CodeHash}");
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(want), Encoding.ASCII.GetBytes(parts[2])) ? m : null;
    }

    private string GroupJson(RelayMember me)
    {
        var sb = new StringBuilder();
        sb.Append("{\"me\":").Append(JsonSerializer.Serialize(me.Name, FsperfJson.Default.String))
          .Append(",\"myPort\":").Append(MyPort(me)).Append(",\"sharing\":[");
        var now = DateTime.UtcNow;
        lock (_lock) {
            bool first = true;
            foreach (var s in _shares.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)) {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":\"").Append(s.MemberId).Append("\",\"name\":").Append(JsonSerializer.Serialize(s.Name, FsperfJson.Default.String))
                  .Append(",\"since\":").Append(JsonSerializer.Serialize(s.Since, FsperfJson.Default.DateTime))
                  .Append(",\"away\":").Append(now - s.LastPush > Away ? "true" : "false")
                  .Append(",\"server\":").Append(s.Events.Last.Contains("\"server\":true") ? "true" : "false").Append('}');   // #17
            }
        }
        return sb.Append("]}").ToString();
    }

    // the viewer's own PC's dashboard port, while that PC shares (0 = unknown: no Settings link, e.g. on a phone).
    // Settings are never changed THROUGH the relay: the link opens the settings page on the viewer's own PC.
    private int MyPort(RelayMember me) { lock (_lock) return _shares.TryGetValue(me.Id, out var s) ? s.Port : 0; }

    private static string SettingsLink(int port) =>
        port > 0 ? $"<a href=\"http://127.0.0.1:{port}/settings\" title=\"SVS Performance Monitor's settings on this PC\">Settings</a>" : "";

    // the dashboard / log page for one member: the group instead of Settings in the menu, their name as the heading
    private static byte[] MemberPage(byte[] page, string name, int myPort)
    {
        string html = Encoding.UTF8.GetString(page);
        string safe = WebUtility.HtmlEncode(name);
        html = html.Replace("<a href=\"/settings\">Settings</a>", SettingsLink(myPort))
                   .Replace("<nav aria-label=\"Main navigation\">", "<nav aria-label=\"Main navigation\"><a href=\"/\">Group</a>")
                   .Replace("<div class=\"product\">SVS Performance Monitor</div>", $"<div class=\"product\">{safe}</div>")
                   .Replace("<div class=\"studio\">SimVizion Studios</div>", "<div class=\"studio\">SVS Performance Monitor · shared</div>")
                   .Replace("<html lang=\"en\">", "<html lang=\"en\" data-relay=\"1\">");
        return Encoding.UTF8.GetBytes(html);
    }

    private static void Redirect(NetworkStream s, string to)
    {
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {to}\r\nContent-Length: 0\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        s.Write(head);
    }

    private static void Send(NetworkStream s, int code, string type, string body, bool cache = false, string? setCookie = null) =>
        Send(s, code, type, Encoding.UTF8.GetBytes(body), cache, setCookie);

    private static void Send(NetworkStream s, int code, string type, byte[] body, bool cache = false, string? setCookie = null)
    {
        string reason = code switch {
            200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed",
            413 => "Payload Too Large", 415 => "Unsupported Media Type", 429 => "Too Many Requests", _ => "Error",
        };
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n");
        sb.Append($"Cache-Control: {(cache ? "max-age=300" : "no-store")}\r\nX-Content-Type-Options: nosniff\r\n");
        sb.Append("Referrer-Policy: no-referrer\r\nX-Frame-Options: DENY\r\nContent-Security-Policy: frame-ancestors 'none'\r\nX-Robots-Tag: noindex, nofollow\r\n");
        if (setCookie != null) sb.Append("Set-Cookie: ").Append(setCookie).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        s.Write(Encoding.ASCII.GetBytes(sb.ToString()));
        s.Write(body);
    }

    public void Dispose()
    {
        _stop = true;
        _members.Removed -= OnMemberRemoved;
        try { _listener.Stop(); } catch { }
        _sweep.Dispose();
        List<Share> all;
        lock (_lock) { all = _shares.Values.ToList(); _shares.Clear(); }
        foreach (var s in all) s.Dispose();
    }
}
