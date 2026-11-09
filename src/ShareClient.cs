// ShareClient.cs - shares this PC's live numbers and game log with a group relay (#14). Only connects out (HTTPS; plain
// HTTP only to a relay on this same PC): no router or firewall change. Every 2 s it sends the newest snapshot and the log
// lines the relay doesn't have yet; the relay answers with the last line it has, so nothing is lost or sent twice when a
// send fails. Before anything leaves the PC, the Windows user name and computer name are replaced (Redactor).
// Copyright (c) 2026 Jared Brown. MIT licence.
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FSPerf;

public sealed class ShareClient : IDisposable
{
    public const int IntervalMs = 2000, FirstLines = 500;

    private readonly Uri _push, _stop;
    private readonly string _code;
    private readonly Func<List<(LogSource Src, LogTail Tail)>> _logs;   // the game's log first, then FSPluginHost's (#15)
    private readonly string _run = Guid.NewGuid().ToString("N")[..12];   // this sharing run: a stop names it
    private readonly Func<Snapshot?> _snapshot;
    private readonly int _localPort;   // this PC's dashboard: the relay offers its Settings link to this member
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _quit = new();   // Dispose: also cancels a send in progress
    private Thread? _thread;
    // per log: the relay's newest line of ours (-1 = none of this tail's run yet) and the tail run that ack belongs to
    private readonly Dictionary<string, (string Epoch, long Ack)> _acks = new();
    private int _turn;   // which log goes first this time: every log gets the byte budget in turn (Codex #15)
    private int _maxLines = RelayServer.MaxLinesPerPush;   // halved when the relay finds a send too big, back up after success

    // what the Settings page and the tray say: "connecting" | "sharing" | "bad-code" | "unreachable"
    public volatile string State = "connecting";
    public volatile string MemberName = "";
    // #16: a group member asked for this PC's full logs (from the relay's last answer); null = no open request
    public volatile LogRequest? Request;
    public sealed record LogRequest(string Id, string By);

    public ShareClient(Uri relay, string code, Func<List<(LogSource Src, LogTail Tail)>> logs, Func<Snapshot?> snapshot, int localPort = 0)
    {
        _localPort = localPort;
        _push = new Uri(relay, "/api/push");
        _stop = new Uri(relay, "/api/stop");
        _code = code;
        _logs = logs;
        _snapshot = snapshot;
        _http = new HttpClient(new SocketsHttpHandler {
            ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,   // the code goes to the address the user typed, never elsewhere
            UseProxy = false,
        }) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SVSPerfMonitor/" + Program.Version);
    }

    // the group's address as typed in Settings ("perf.example.com:9443", "https://perf.example.com:9443/") -> its base URL,
    // or null + why. HTTPS always, except to this PC itself (a host sharing with its own relay).
    public static Uri? ParseAddress(string text, out string? problem)
    {
        problem = null;
        string t = (text ?? "").Trim();
        if (t.Length == 0) { problem = "Enter the group’s address."; return null; }
        if (!t.Contains("://")) t = "https://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u) || u.Host.Length == 0 || u.UserInfo.Length > 0) {
            problem = "Enter an address like perf.example.com:9443."; return null;
        }
        bool local = u.IsLoopback;
        if (u.Scheme != Uri.UriSchemeHttps && !(local && u.Scheme == Uri.UriSchemeHttp)) {
            problem = "The group’s address must start with https://."; return null;
        }
        return new Uri($"{u.Scheme}://{u.Authority}/");
    }

    public void Start()
    {
        _thread ??= new Thread(Loop) { IsBackground = true, Name = "svsperf-share" };
        _thread.Start();
    }

    private void Loop()
    {
        int wait = 0;
        while (!_quit.Token.WaitHandle.WaitOne(wait)) {
            int next;
            try { next = PushOnce(); }
            catch (OperationCanceledException) when (_quit.IsCancellationRequested) { return; }
            catch { State = "unreachable"; next = 15000; }
            wait = next;
        }
    }

    // one send; the time to wait before the next.
    // Body: {"run","port","snapshot","logs":[{"id","epoch","after","lines":[[seq,text,marker],...]}]} (the game's log
    // first). Answer: {"name","acks":{"<id>":n},"resync":["<id>",...]}: per log, what comes after its ack goes next; a
    // log in "resync" is one the relay doesn't know (it restarted or forgot us): its recent history goes again.
    private int PushOnce()
    {
        var logs = _logs();
        var sb = new StringBuilder(4096);
        sb.Append("{\"run\":\"").Append(_run).Append("\",\"port\":").Append(_localPort).Append(",\"snapshot\":");
        var snap = _snapshot();
        // relaxed escaping keeps non-ASCII letters as they are, so the redaction sees "Jörg", not "J\u00F6rg"
        sb.Append(snap != null ? Redactor.Apply(JsonSerializer.Serialize(snap, FsperfJson.RelaxedSnapshot)) : "null");
        sb.Append(",\"all\":[").Append(string.Join(",", logs.Select(l => JsonSerializer.Serialize(l.Src.Id, FsperfJson.Default.String)))).Append(']');
        sb.Append(",\"logs\":[");
        int bytes = Encoding.UTF8.GetByteCount(sb.ToString());
        bool more = false, firstLog = true;
        int start = logs.Count == 0 ? 0 : _turn++ % logs.Count;
        foreach (var (src, tail) in logs.Skip(start).Concat(logs.Take(start))) {
            string epoch = tail.Epoch;
            long after = _acks.TryGetValue(src.Id, out var a) && a.Epoch == epoch ? a.Ack : -1;
            List<LogLine> lines;
            lock (tail.Gate) lines = after < 0 ? tail.Snapshot(-1, FirstLines, out _) : tail.Snapshot(after, _maxLines, out _);
            if (lines.Count > _maxLines) { lines = lines.GetRange(0, _maxLines); more = true; }   // the oldest first: the rest follows
            var head = new StringBuilder();
            head.Append(firstLog ? "" : ",").Append("{\"id\":").Append(JsonSerializer.Serialize(src.Id, FsperfJson.Default.String))
                .Append(",\"epoch\":").Append(JsonSerializer.Serialize(epoch, FsperfJson.Default.String))
                .Append(",\"after\":").Append(after).Append(",\"lines\":[");
            int hb = Encoding.UTF8.GetByteCount(head.ToString()) + 2;
            if (bytes + hb > Budget) { more = true; break; }
            sb.Append(head); bytes += hb; firstLog = false;
            int sent = 0;
            foreach (var l in lines) {
                string item = "[" + l.Seq + "," + JsonSerializer.Serialize(Redactor.Apply(l.Text), FsperfJson.LogText) + (l.Marker ? ",1]" : ",0]");
                int b = Encoding.UTF8.GetByteCount(item) + 1;
                if (bytes + b > Budget) { more = true; break; }   // in BYTES, as the relay counts: the rest goes next time
                if (sent > 0) sb.Append(',');
                sb.Append(item);
                bytes += b; sent++;
            }
            sb.Append("]}");
        }
        sb.Append("]}");
        using var req = new HttpRequestMessage(HttpMethod.Post, _push) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _code);
        using var resp = _http.Send(req, _quit.Token);
        string body = new StreamReader(resp.Content.ReadAsStream(_quit.Token)).ReadToEnd();
        switch (resp.StatusCode) {
            case HttpStatusCode.OK: {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var epochs = logs.ToDictionary(l => l.Src.Id, l => l.Tail.Epoch);
                if (root.TryGetProperty("acks", out var acks) && acks.ValueKind == JsonValueKind.Object)
                    foreach (var p in acks.EnumerateObject())
                        if (epochs.TryGetValue(p.Name, out var ep) && p.Value.TryGetInt64(out long ack)) _acks[p.Name] = (ep, ack);
                bool resync = false;
                if (root.TryGetProperty("resync", out var rs) && rs.ValueKind == JsonValueKind.Array)
                    foreach (var id in rs.EnumerateArray()) if (id.GetString() is { } sid) { _acks.Remove(sid); resync = true; }
                if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) MemberName = n.GetString() ?? "";
                Request = root.TryGetProperty("request", out var rq) && rq.ValueKind == JsonValueKind.Object
                    && rq.TryGetProperty("id", out var ri) && ri.ValueKind == JsonValueKind.String && ri.GetString() is { Length: > 0 and <= 32 } rid
                    && rq.TryGetProperty("by", out var rb) && rb.ValueKind == JsonValueKind.String
                    ? new LogRequest(rid, Invites.CleanName(rb.GetString())) : null;
                State = "sharing";
                _maxLines = Math.Min(RelayServer.MaxLinesPerPush, _maxLines * 2);
                return more || resync ? 600 : IntervalMs;   // catching up: a little faster
            }
            case HttpStatusCode.Conflict: return IntervalMs;   // "stopped": a stop of this run is in effect
            case HttpStatusCode.RequestEntityTooLarge: _maxLines = Math.Max(1, _maxLines / 2); return 600;
            case HttpStatusCode.Unauthorized: State = "bad-code"; return 60000;
            case HttpStatusCode.TooManyRequests: return 5000;
            default: State = "unreachable"; return 15000;
        }
    }

    private const int Budget = RelayServer.MaxBody - 16 * 1024;

    // ---- #16 full logs on request ----
    // the files themselves (each log's whole file, the newest 64 MB at most), every line cleaned like the live view,
    // in one zip. null = sent; else why not
    public string? SendFullLogs(string requestId)
    {
        byte[] zip;
        try { zip = BuildFullLogsZip(_logs().Select(l => l.Src)); }
        catch (Exception e) { return "the logs couldn't be packed: " + e.Message; }
        if (zip.Length > RelayServer.MaxFullLogs) return $"the logs are too big to send, even packed ({zip.Length / 1048576} MB; at most {RelayServer.MaxFullLogs / 1048576} MB).";
        try {
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = TimeSpan.FromMinutes(3) };
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_push, "/api/full-logs/send")) { Content = new ByteArrayContent(zip) };
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _code);
            req.Headers.TryAddWithoutValidation("X-SVS-Request", requestId);
            using var resp = http.Send(req);
            if (resp.IsSuccessStatusCode) { Request = null; return null; }
            string body = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
            try { return JsonSerializer.Deserialize(body, FsperfJson.Default.String) ?? $"the group answered {(int)resp.StatusCode}."; }
            catch { return $"the group answered {(int)resp.StatusCode}."; }
        } catch (Exception e) { return "the group couldn't be reached: " + e.Message; }
    }

    public void DeclineFullLogs()
    {
        Request = null;
        try {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_push, "/api/full-logs/decline")) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _code);
            using var _ = _http.Send(req);
        } catch { }
    }

    public const long FullLogsFileBytes = 64L * 1024 * 1024;

    public static byte[] BuildFullLogsZip(IEnumerable<LogSource> sources)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true)) {
            foreach (var src in sources) {
                if (src.Path.Length == 0 || !File.Exists(src.Path)) continue;
                string entry = src.Id switch {
                    LogSources.Game => "log.txt", "fsp" => "fsp.log", "loader" => "fs25plugins.log",
                    _ => "plugins/" + src.Id[7..] + ".log",   // a plain name (LogSources.ValidPluginName)
                };
                using var fs = new FileStream(src.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                bool cut = fs.Length > FullLogsFileBytes;
                if (cut) fs.Seek(fs.Length - FullLogsFileBytes, SeekOrigin.Begin);
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                using var w = new StreamWriter(zip.CreateEntry(entry, System.IO.Compression.CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                if (cut) { reader.ReadLine(); w.WriteLine($"=== the first {(fs.Length - FullLogsFileBytes) / 1048576} MB of this file were left out (it is very long) ==="); }
                string? line;
                while ((line = reader.ReadLine()) != null) w.WriteLine(Redactor.Apply(line));
            }
        }
        return ms.ToArray();
    }

    // stops sharing: the send in progress is cancelled and finished FIRST, then the relay is told (so no late send can
    // bring the share back; the relay also refuses later sends of this run), briefly and best effort
    public void Dispose()
    {
        _quit.Cancel();
        _thread?.Join(5000);
        try {
            using var cts = new CancellationTokenSource(2000);
            string body = "{\"run\":\"" + _run + "\"}";
            using var req = new HttpRequestMessage(HttpMethod.Post, _stop) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _code);
            using var _ = _http.Send(req, cts.Token);
        } catch { }
        _http.Dispose();
        _quit.Dispose();
    }
}

// what must not leave the PC: the user's personal folders (their names often carry a real name: Documents may live in
// D:\Users\<name>\ even when the Windows user name is something else), the Windows user name and the computer name.
// Paths are matched with any separator, also JSON-escaped ones (\\), case-insensitively:
//   1. the user's own folders wherever they are (Documents, Desktop, AppData, the profile...) -> [Documents], [User]...
//   2. any <drive>:\Users\<name> -> <drive>:\Users\user
//   3. the bare user name and computer name as whole words (2+ characters; a short name may also hide an ordinary word)
public static class Redactor
{
    private const string Sep = @"[\\/]{1,2}";
    private static readonly List<(Regex Rx, string With)> s_rules = Build();

    private static List<(Regex, string)> Build()
    {
        const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var rules = new List<(Regex, string)>();
        var folders = new List<(string Path, string Label)>();
        void Add(Environment.SpecialFolder f, string label)
        {
            try { string p = Environment.GetFolderPath(f); if (p.Length > 3) folders.Add((p.TrimEnd('\\', '/'), label)); } catch { }
        }
        Add(Environment.SpecialFolder.MyDocuments, "[Documents]");
        Add(Environment.SpecialFolder.DesktopDirectory, "[Desktop]");
        Add(Environment.SpecialFolder.LocalApplicationData, "[LocalAppData]");
        Add(Environment.SpecialFolder.ApplicationData, "[AppData]");
        Add(Environment.SpecialFolder.UserProfile, "[User]");
        string? oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(oneDrive) && oneDrive.Length > 3) folders.Add((oneDrive.TrimEnd('\\', '/'), "[OneDrive]"));
        // the longest first: Documents inside the profile is replaced as [Documents], not as [User]\Documents
        foreach (var (path, label) in folders.OrderByDescending(f => f.Path.Length)) {
            var segs = path.Split('\\', '/').Where(s => s.Length > 0).Select(Regex.Escape);
            rules.Add((new Regex("(?<![A-Za-z0-9])" + string.Join(Sep, segs) + @"(?=[\\/""'\s:;,)\]]|$)", O), label));
        }
        rules.Add((new Regex(@"(?<=\b[A-Za-z]:" + Sep + "Users" + Sep + @")(?!user(?:[\\/""]|$))[^\\/""\r\n]+", O), "user"));
        string user = Environment.UserName ?? "", pc = Environment.MachineName ?? "";
        if (user.Length >= 2 && !user.Equals("user", StringComparison.OrdinalIgnoreCase)) rules.Add((new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(user) + @"(?![\p{L}\p{N}_])", O), "user"));
        if (pc.Length >= 2) rules.Add((new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(pc) + @"(?![\p{L}\p{N}_])", O), "PC"));
        return rules;
    }

    public static string Apply(string text)
    {
        foreach (var (rx, with) in s_rules) text = rx.Replace(text, with);
        return text;
    }
}
