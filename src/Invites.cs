// Invites.cs - one-click joining (#18). An invite is a link to the group's join page with the invite code in the
// fragment (https://perf.example.com/join#ABCD-...): a browser never sends the fragment to any server, so the code is in
// no request line or log. The join page signs that browser in and offers "Share this PC", which opens this app through
// the svsperf: link type (svsperf://join/?a=<group origin>&c=<code>); the app then asks the user before anything
// changes. Copyright (c) 2026 Jared Brown. MIT licence.
using Microsoft.Win32;

namespace FSPerf;

public static class Invites
{
    public const string Scheme = "svsperf";

    // the invite link for a member: the group's address (as typed by the host) + /join#<code>
    public static string Link(string groupAddress, string code)
    {
        var u = ShareClient.ParseAddress(groupAddress, out _);
        return u == null ? "" : new Uri(u, "/join").AbsoluteUri + "#" + code;
    }

    // an invite in any form a user may paste or a link may bring: https://<group>/join#<code>, svsperf://join/?a=..&c=..
    // -> the group's base address and the code. false + why when it isn't one.
    public static bool TryParse(string? text, out Uri? group, out string code, out string? why)
    {
        group = null; code = ""; why = null;
        string t = (text ?? "").Trim().Trim('﻿', '​').Trim();   // pasted or piped text may carry a BOM / zero-width space
        if (t.Length == 0 || t.Length > 2048) { why = "Paste the invite link you got from your group’s host."; return false; }
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) { why = "That doesn’t look like an invite link."; return false; }
        string address;
        if (u.Scheme == Scheme) {
            string? a = null, c = null;
            foreach (var kv in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)) {
                int eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                string k = kv[..eq], v;
                try { v = Uri.UnescapeDataString(kv[(eq + 1)..]); } catch { continue; }
                if (k == "a") a = v; else if (k == "c") c = v;
            }
            if (a == null || c == null) { why = "That invite link is incomplete."; return false; }
            address = a; code = c;
        } else if (u.Scheme is "https" or "http") {
            if (!u.AbsolutePath.TrimEnd('/').EndsWith("/join", StringComparison.OrdinalIgnoreCase) || u.Fragment.Length < 2) {
                why = "That isn’t an invite link: it should end in /join#… ."; return false;
            }
            address = $"{u.Scheme}://{u.Authority}"; code = Uri.UnescapeDataString(u.Fragment[1..]);
        } else { why = "That doesn’t look like an invite link."; return false; }
        group = ShareClient.ParseAddress(address, out why);
        if (group == null) return false;
        string n = RelayMembers.Normalize(code);
        if (n.Length != 24) { group = null; code = ""; why = "The invite code in that link is incomplete."; return false; }
        code = string.Join("-", Enumerable.Range(0, 6).Select(i => n.Substring(i * 4, 4)));
        return true;
    }

    // A web page picks an svsperf: link's text, and some launchers may let it break out of its quotes. So whenever the
    // command line mentions joining at all, it must be EXACTLY `--tray --join <one link>` (+ the internal --tray-child):
    // checked first thing in Main, before any option does anything (Codex #18: --exit, a UNC --settings path...).
    // null = not a join; "" = a join, but malformed (refuse); else the link.
    public static string? JoinInvocation(string[] args)
    {
        bool mentions = args.Any(a => a.Equals("--join", StringComparison.OrdinalIgnoreCase)
                                   || a.Contains(Scheme + ":", StringComparison.OrdinalIgnoreCase));
        if (!mentions) return null;
        var a2 = args.Length > 0 && args[^1] == "--tray-child" ? args[..^1] : args;
        return a2.Length == 3 && a2[0] == "--tray" && a2[1] == "--join" && a2[2].StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase)
            && !a2[2].Any(char.IsControl) ? a2[2] : "";
    }

    // the requests an invite link brought, one file each in the settings folder (join\<time>-<id>.req: "<group>\n<code>"),
    // picked up by whichever copy runs (also one with full permissions, which other copies can't signal); read once,
    // then deleted; older than 5 minutes = deleted unread
    public static string RequestDir => Path.Combine(Path.GetDirectoryName(SettingsStore.FilePath)!, "join");

    public static void WriteRequest(Uri group, string code)
    {
        Directory.CreateDirectory(RequestDir);
        string file = Path.Combine(RequestDir, $"{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}.req");
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, group.AbsoluteUri + "\n" + code);
        File.Move(tmp, file);   // complete or not there at all
    }

    // requests (and half-written files) older than 5 minutes go unread: called every second, also while a question is open
    public static void ExpireRequests()
    {
        try {
            if (!Directory.Exists(RequestDir)) return;
            foreach (var f in Directory.GetFiles(RequestDir))
                try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromMinutes(5)) File.Delete(f); } catch { }
        } catch { }
    }

    // the oldest waiting request (deleted as it is taken), or null
    public static (Uri Group, string Code)? TakeRequest()
    {
        try {
            if (!Directory.Exists(RequestDir)) return null;
            foreach (var f in Directory.GetFiles(RequestDir, "*.req").OrderBy(f => f, StringComparer.Ordinal)) {
                string text;
                try { text = File.ReadAllText(f); File.Delete(f); } catch { continue; }
                var parts = text.Split('\n');
                if (parts.Length == 2 && ShareClient.ParseAddress(parts[0], out _) is { } g && RelayMembers.Normalize(parts[1]).Length == 24)
                    return (g, parts[1].Trim());
            }
        } catch { }
        return null;
    }

    // a name another server sent: shown in our own question, so no line breaks, control or direction characters, short
    public static string CleanName(string? name)
    {
        var s = new string((name ?? "").Where(ch => char.GetUnicodeCategory(ch) is not (System.Globalization.UnicodeCategory.Control
            or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator or System.Globalization.UnicodeCategory.Surrogate
            or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.OtherNotAssigned)).ToArray()).Trim();
        return s.Length > 40 ? s[..40].Trim() + "…" : s;
    }

    // registers svsperf: for this Windows user, pointing at this copy's starter (`--tray --join "<link>"`: the starter
    // starts the tray app, which hands the request to a running copy). Refreshed at every start, so it follows the copy
    // the user runs. Only the real tray app does this (the self-test's instances use other names).
    public static void RegisterLinkType(string starter)
    {
        try {
            using var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            k.SetValue("", "URL:SVS Performance Monitor");
            k.SetValue("URL Protocol", "");
            using (var icon = k.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{starter}\",0");
            using var cmd = k.CreateSubKey(@"shell\open\command");
            string want = $"\"{starter}\" --tray --join \"%1\"";
            if (cmd.GetValue("") as string != want) cmd.SetValue("", want);
        } catch { }   // joining by link is a convenience; pasting the link in Settings still works
    }
}
