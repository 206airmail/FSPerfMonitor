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
    private readonly LogTail _tail;
    private readonly Func<Snapshot?> _snapshot;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _quit = new();   // Dispose: also cancels a send in progress
    private Thread? _thread;
    private long _ack = -1;          // the relay's newest line of ours (-1 = it has none of this run yet)
    private string _ackEpoch = "";
    private int _maxLines = RelayServer.MaxLinesPerPush;   // halved when the relay finds a send too big, back up after success

    // what the Settings page and the tray say: "connecting" | "sharing" | "bad-code" | "unreachable"
    public volatile string State = "connecting";
    public volatile string MemberName = "";

    public ShareClient(Uri relay, string code, LogTail tail, Func<Snapshot?> snapshot)
    {
        _push = new Uri(relay, "/api/push");
        _stop = new Uri(relay, "/api/stop");
        _code = code;
        _tail = tail;
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

    // one send; the time to wait before the next
    private int PushOnce()
    {
        string epoch = _tail.Epoch;
        if (epoch != _ackEpoch) { _ack = -1; _ackEpoch = epoch; }
        long after = _ack;
        List<LogLine> lines;
        lock (_tail.Gate) lines = after < 0 ? _tail.Snapshot(-1, FirstLines, out _) : _tail.Snapshot(after, _maxLines, out _);
        if (lines.Count > _maxLines) lines = lines.GetRange(0, _maxLines);   // the oldest first: the rest follows
        var sb = new StringBuilder(256 + lines.Count * 120);
        sb.Append("{\"epoch\":").Append(JsonSerializer.Serialize(epoch, FsperfJson.Default.String))
          .Append(",\"after\":").Append(after).Append(",\"snapshot\":");
        var snap = _snapshot();
        // relaxed escaping keeps non-ASCII letters as they are, so the redaction sees "Jörg", not "J\u00F6rg"
        sb.Append(snap != null ? Redactor.Apply(JsonSerializer.Serialize(snap, FsperfJson.RelaxedSnapshot)) : "null");
        sb.Append(",\"lines\":[");
        int bytes = Encoding.UTF8.GetByteCount(sb.ToString()), sent = 0;
        for (int i = 0; i < lines.Count; i++) {
            string item = "[" + lines[i].Seq + "," + JsonSerializer.Serialize(Redactor.Apply(lines[i].Text), FsperfJson.LogText) + (lines[i].Marker ? ",1]" : ",0]");
            int b = Encoding.UTF8.GetByteCount(item) + 1;
            if (bytes + b > RelayServer.MaxBody - 16 * 1024) break;   // in BYTES, as the relay counts: the rest goes next time
            if (i > 0) sb.Append(',');
            sb.Append(item);
            bytes += b; sent++;
        }
        sb.Append("]}");
        using var req = new HttpRequestMessage(HttpMethod.Post, _push) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _code);
        using var resp = _http.Send(req, _quit.Token);
        string body = new StreamReader(resp.Content.ReadAsStream(_quit.Token)).ReadToEnd();
        switch (resp.StatusCode) {
            case HttpStatusCode.OK: {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("ack", out var a) && a.TryGetInt64(out long ack)) _ack = ack;
                if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) MemberName = n.GetString() ?? "";
                State = "sharing";
                _maxLines = Math.Min(RelayServer.MaxLinesPerPush, _maxLines * 2);
                return sent < lines.Count || lines.Count >= _maxLines ? 600 : IntervalMs;   // catching up: a little faster
            }
            case HttpStatusCode.Conflict:
                // the relay lost our run (it restarted, or forgot us after a quiet spell): our recent history again
                if (body.Contains("resync")) { _ack = -1; return 600; }
                return IntervalMs;   // "stopped": a stop of this run is in effect
            case HttpStatusCode.RequestEntityTooLarge: _maxLines = Math.Max(1, _maxLines / 2); return 600;
            case HttpStatusCode.Unauthorized: State = "bad-code"; return 60000;
            case HttpStatusCode.TooManyRequests: return 5000;
            default: State = "unreachable"; return 15000;
        }
    }

    // stops sharing: the send in progress is cancelled and finished FIRST, then the relay is told (so no late send can
    // bring the share back; the relay also refuses later sends of this run), briefly and best effort
    public void Dispose()
    {
        _quit.Cancel();
        _thread?.Join(5000);
        try {
            using var cts = new CancellationTokenSource(2000);
            string body = "{\"epoch\":" + JsonSerializer.Serialize(_tail.Epoch, FsperfJson.Default.String) + "}";
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
