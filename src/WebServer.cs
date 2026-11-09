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
    string SetupFrameTimes();      // the frame-times state after the setup: ready / signout / cancelled / failure
}

public sealed class WebServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly EventHub _events = new(16);
    private readonly SemaphoreSlim _downloads = new(2, 2);   // log downloads at a time
    private readonly LogTail? _gameTail;
    private readonly Dictionary<string, LogHub> _logHubs = new();   // log source id -> its viewers (#15; under _lock)
    private readonly byte[] _logPage;
    private const int RequestDeadlineMs = 10000;
    private readonly object _lock = new();
    private readonly byte[] _page;
    private volatile bool _stop;

    // lan = also reachable from other computers (all interfaces); still no URL reservation (a raw TcpListener), but Windows
    // Firewall must allow the port inbound (Program prints the rule when it is missing)
    public WebServer(int port, bool lan = false, LogTail? tail = null)
    {
        _port = port; _lanMode = lan;
        _hosts = BuildHosts(); _hostsAt = DateTime.UtcNow;
        _gameTail = tail;
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
        using var s4 = Assembly.GetExecutingAssembly().GetManifestResourceStream("welcome.html")!;
        using var ms4 = new MemoryStream();
        s4.CopyTo(ms4);
        _welcomePage = ms4.ToArray();
    }

    private readonly byte[] _settingsPage, _welcomePage;

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
            string lastEventId = "", hostHeader = "", origin = "", contentType = "", saveToken = "";
            while (true) {
                string? h = ReadLineBounded(stream, 8192, deadline);
                if (h == null || ++headers > 100) { c.Close(); return; }
                if (h.Length == 0) break;  // end of the headers
                if (h.StartsWith("Last-Event-ID:", StringComparison.OrdinalIgnoreCase)) lastEventId = h[14..].Trim();
                else if (h.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) hostHeader = h[5..].Trim();
                else if (h.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase)) origin = h[7..].Trim();
                else if (h.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)) contentType = h[13..].Trim();
                else if (h.StartsWith("X-SVS-Token:", StringComparison.OrdinalIgnoreCase)) saveToken = h[12..].Trim();
                else if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(h[15..].Trim(), out contentLength);
            }
            if (hostHeader.Length == 0) { Send(stream, 400, "text/plain", Encoding.ASCII.GetBytes("Host header required")); c.Close(); return; }
            if (!HostAllowed(hostHeader)) { Send(stream, 403, "text/plain", Encoding.ASCII.GetBytes("forbidden host")); c.Close(); return; }   // DNS rebinding guard
            string path = line.Split(' ').ElementAtOrDefault(1) ?? "/";
            string route = path.Split('?')[0];
            if (line.StartsWith("POST ") && (route is "/api/settings" or "/api/frametimes-setup" or "/api/group-members")) {
                HandleSaveSettings(c, stream, origin, contentType, saveToken, contentLength, deadline, route);
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
            if (route == "/welcome") { Send(stream, 200, "text/html; charset=utf-8", _welcomePage); c.Close(); return; }
            if (Assets.TryGetValue(route, out var asset)) { Send(stream, 200, asset.Type, asset.Body, cache: true); c.Close(); return; }
            if (path == "/events") { _events.Serve(c, stream); return; }   // kept open; Publish writes to it
            if (route == "/api/logs") { Send(stream, 200, "application/json", Encoding.UTF8.GetBytes(LogListJson(Host?.Logs().Select(l => l.Src) ?? []))); c.Close(); return; }
            if (route == "/log/stream" && HubFor(SourceOf(path)) is { } hub) {
                hub.Serve(c, stream, path, lastEventId);
                return;  // kept open; the tail writes to it
            }
            if (route == "/log/download" && HubFor(SourceOf(path)) is { } dhub) {
                string src = SourceOf(path);
                if (!_downloads.Wait(0)) Send(stream, 503, "text/plain", Encoding.ASCII.GetBytes("busy, try again in a moment"));
                else try { dhub.Download(stream, src == LogSources.Game ? "game" : LogSources.LabelFor(src)); } finally { _downloads.Release(); }
                c.Close();
                return;
            }
            if (route == "/log") { Send(stream, 200, "text/html; charset=utf-8", _logPage); c.Close(); return; }
            if (path == "/api/snapshot") Send(stream, 200, "application/json", Encoding.UTF8.GetBytes(_events.Last));
            else if (path == "/" || path.StartsWith("/?")) Send(stream, 200, "text/html; charset=utf-8", _page);
            else Send(stream, 404, "text/plain", Encoding.ASCII.GetBytes("not found"));
            c.Close();
        } catch {
            try { c.Close(); } catch { }
        }
    }

    // ---- log sources (#15) --------------------------------------------------------------------------------------------
    // ?src=<id> on /log/stream and /log/download ("game" when missing); the hub is made on first use for a known source
    internal static string SourceOf(string path)
    {
        int q = path.IndexOf('?');
        if (q >= 0) {
            foreach (var kv in path[(q + 1)..].Split('&')) {
                if (!kv.StartsWith("src=", StringComparison.Ordinal)) continue;
                string v;
                try { v = Uri.UnescapeDataString(kv[4..]); } catch { return ""; }
                return LogSources.ValidId(v) ? v : "";
            }
        }
        return LogSources.Game;
    }

    private LogHub? HubFor(string id)
    {
        if (id.Length == 0) return null;
        lock (_lock) {
            if (_stop) return null;
            if (_logHubs.TryGetValue(id, out var h)) return h;
            var tail = Host?.TailFor(id) ?? (id == LogSources.Game ? _gameTail : null);
            if (tail == null) return null;
            return _logHubs[id] = new LogHub(tail, 8);
        }
    }

    // GET /api/logs: [{"id","label"}] in the page's order (the game's log first)
    internal static string LogListJson(IEnumerable<LogSource> sources)
    {
        var list = sources.ToList();
        if (list.Count == 0) list.Add(new LogSource(LogSources.Game, LogSources.LabelFor(LogSources.Game), ""));
        string S(string v) => System.Text.Json.JsonSerializer.Serialize(v, FsperfJson.Default.String);
        return "[" + string.Join(",", list.Select(l => $"{{\"id\":{S(l.Id)},\"label\":{S(l.Label)}}}")) + "]";
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
    internal static string? ReadLineBounded(NetworkStream s, int max, DateTime deadline)
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

    internal static void Send(NetworkStream s, int code, string type, byte[] body, bool cache = false)
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
    public MonitorHost? Host { get; set; }   // #14: the sharing state and the hosted group

    private static bool IsLocal(TcpClient c) =>
        c.Client.RemoteEndPoint is IPEndPoint ep && IPAddress.IsLoopback(ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address);

    // a second lock besides the Origin: a random token per server start that only this PC's Settings page can read (GET
    // /api/settings gives it to local viewers; other sites can't read that answer without CORS, which this server never
    // allows), sent back in the X-SVS-Token header
    private readonly string _saveToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    private bool TokenMatches(string token) =>
        token.Length == _saveToken.Length &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(_saveToken));

    private bool OriginIsThisDashboard(string origin) =>
        origin == $"http://127.0.0.1:{_port}" || origin == $"http://localhost:{_port}" || origin == $"http://[::1]:{_port}";

    private string SettingsViewJson(bool local)
    {
        var b = Settings;
        string why = b == null ? "These settings belong to the tray app. This copy was started from the command line."
            : !local ? "Settings can be changed on the PC running SVS Performance Monitor." : "";
        bool canEdit = b != null && local;
        string frames = !FrameMonitor.PresentMonAvailable(null) ? "missing" : AutoStart.FrameTimesAccess();
        var settings = b?.Current ?? new AppSettings();
        if (!local) settings.InviteCode = "";   // a secret: never to another device
        var h = Host;
        var members = local && h?.GroupMembers != null
            ? h.GroupMembers.All().Select(m => new GroupMemberView(m.Id, m.Name, m.Added)).ToList() : new List<GroupMemberView>();
        var view = new SettingsView(settings, canEdit, why, SettingsStore.DefaultProfileFolder,
            Program.Version, $"http://127.0.0.1:{_port}/", canEdit ? _saveToken : "", frames, AutoStart.IsElevated(),
            _lanMode ? AutoStart.HomeNetworkAddresses().Select(ip => $"http://{ip}:{_port}/").ToList() : new List<string>(),
            h?.ShareState ?? "off", h?.ShareName ?? "", h?.GroupMembers != null && h.GroupProblem.Length == 0, h?.GroupProblem ?? "", members);
        return System.Text.Json.JsonSerializer.Serialize(view, FsperfJson.Default.SettingsView);
    }

    private void HandleSaveSettings(TcpClient c, NetworkStream stream, string origin, string contentType, string token, int length,
        DateTime deadline, string route)
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
        if (!IsLocal(c) || !OriginIsThisDashboard(origin) || !TokenMatches(token)) { Send(stream, 403, "application/json", Err("Settings can be changed on the PC running SVS Performance Monitor.")); return; }
        if (!contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) { Send(stream, 415, "application/json", Err("JSON only")); return; }
        if (Settings == null) { Send(stream, 409, "application/json", Err("These settings belong to the tray app. This copy was started from the command line.")); return; }
        if (route == "/api/group-members") { GroupMembers(stream, body, Err); return; }
        if (route == "/api/frametimes-setup") {   // POST /api/frametimes-setup (body "{}"): a Windows permission prompt, waits for it
            Send(stream, 200, "application/json", Err(Settings.SetupFrameTimes()));
            return;
        }
        AppSettings? s;
        try { s = System.Text.Json.JsonSerializer.Deserialize(body, FsperfJson.Default.AppSettings); } catch { s = null; }
        if (s == null) { Send(stream, 400, "application/json", Err("not settings")); return; }
        string? problem = Settings.Save(s);
        if (problem != null) { Send(stream, 422, "application/json", Err(problem)); return; }
        Send(stream, 200, "application/json", Encoding.UTF8.GetBytes(SettingsViewJson(true)));
        Settings.Saved();   // after the answer is sent: the tray app restarts the monitor (and this server) with them
    }

    // POST /api/group-members {"add":"<name>"} -> {"id","name","code"} (the code is shown once) | {"remove":"<id>"} -> {}
    // Same locks as saving the settings (this PC, this page, the token): the group is managed only on the host's PC.
    private void GroupMembers(NetworkStream stream, byte[] body, Func<string, byte[]> err)
    {
        var members = Host?.GroupMembers;
        if (members == null) { Send(stream, 409, "application/json", err("Turn on hosting a group and save first.")); return; }
        try {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("add", out var add) && add.ValueKind == System.Text.Json.JsonValueKind.String) {
                var (m, code, problem) = members.Add(add.GetString() ?? "");
                if (m == null) { Send(stream, 422, "application/json", err(problem ?? "not added")); return; }
                string S(string v) => System.Text.Json.JsonSerializer.Serialize(v, FsperfJson.Default.String);
                Send(stream, 200, "application/json", Encoding.UTF8.GetBytes($"{{\"id\":{S(m.Id)},\"name\":{S(m.Name)},\"code\":{S(code)}}}"));
                return;
            }
            if (root.TryGetProperty("remove", out var rm) && rm.ValueKind == System.Text.Json.JsonValueKind.String) {
                if (!members.Remove(rm.GetString() ?? "")) { Send(stream, 404, "application/json", err("Not in the group.")); return; }
                Send(stream, 200, "application/json", Encoding.UTF8.GetBytes("{}"));
                return;
            }
        } catch { }
        Send(stream, 400, "application/json", err("bad request"));
    }

    // the page's static files (embedded): the design's style sheet and the brand art
    internal static readonly Dictionary<string, (string Type, byte[] Body)> Assets = LoadAssets();

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

    public void Publish(string json) => _events.Publish(json);

    // also closes every open live view (dashboard and log): their browsers reconnect, to the restarted server after a
    // settings change, instead of hanging on to this one
    public void Dispose()
    {
        _stop = true;
        try { _listener.Stop(); } catch { }
        _events.Dispose();
        List<LogHub> hubs;
        lock (_lock) { hubs = _logHubs.Values.ToList(); _logHubs.Clear(); }
        foreach (var h in hubs) h.Dispose();
    }
}
