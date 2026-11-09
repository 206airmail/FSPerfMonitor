// LogTailSelfTest.cs - `fsperf --logtail-selftest`: LogTail against temp files (never the game's log). Prints "ok"/"FAIL" lines,
// exit code = number of failures. Run by test\selftest.ps1. Copyright (c) 2026 Jared Brown. MIT licence.
using System.Text;

namespace FSPerf;

public static class LogTailSelfTest
{
    private static int _fails;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok) _fails++;
    }

    private static void Append(string path, string text, bool bytesOnly = false)
    {
        using var f = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var b = new UTF8Encoding(false).GetBytes(text);
        f.Write(b, 0, b.Length);
    }

    private static string[] Texts(IEnumerable<LogLine> l) => l.Select(x => x.Text).ToArray();

    public static int Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fsperf_logtail_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string log = Path.Combine(dir, "log.txt");
        try {
            // a relay ring with a character budget (#15 review): the oldest lines go, the newest stay, numbering goes on
            using (var b = LogTail.Detached(100, charBudget: 50)) {
                for (int i = 0; i < 10; i++) b.Append(new List<LogLine> { new(0, $"line{i:00}....", false) });   // 10 characters each
                var kept = b.Snapshot(0, 100, out bool gap);
                Check(kept.Count == 5 && kept[^1].Text == "line09...." && kept[0].Text == "line05...." && gap && b.LastSeq == 10,
                    $"budgeted ring keeps the newest lines within its characters ({kept.Count} kept, gap {gap})");
                for (int i = 0; i < 200; i++) b.Append(new List<LogLine> { new(0, "x", false) });   // wraps the ring: counts stay right
                Check(b.Snapshot(0, 100, out _).Count == 50, "budgeted ring: after wrapping, the ring size is the limit for short lines");
            }
            // no file yet: nothing, no crash
            using (var t0 = new LogTail(log, 100, 100)) Check(!t0.Poll() && t0.LastSeq == 0, "missing file: no lines, no crash");

            // the game holds the file open for writing while we read
            using var game = new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            void Game(string s) { var b = new UTF8Encoding(false).GetBytes(s); game.Write(b, 0, b.Length); game.Flush(); }

            Game("line one\r\nline two\n");
            using var t = new LogTail(log, 50, 100);
            var seen = new List<LogLine>();
            t.Appended += l => seen.AddRange(l);
            Check(t.Poll(), "existing content is read on the first poll");
            Check(Texts(seen).SequenceEqual(new[] { "line one", "line two" }), "lines split on LF/CRLF, CR removed");
            Check(!t.Poll(), "no growth: poll does nothing");

            seen.Clear();
            Game("part");
            Check(!t.Poll() && seen.Count == 0, "partial last line is held back");
            Game("ial done\nnext äö€ \U0001F600\n");
            t.Poll();
            Check(Texts(seen).SequenceEqual(new[] { "partial done", "next äö€ \U0001F600" }), "partial line completed later; UTF-8 intact");

            // a multibyte character split across two reads
            seen.Clear();
            var euro = new UTF8Encoding(false).GetBytes("€\n");
            game.Write(euro, 0, 2); game.Flush();
            t.Poll();
            game.Write(euro, 2, euro.Length - 2); game.Flush();
            t.Poll();
            Check(Texts(seen).SequenceEqual(new[] { "€" }), "a UTF-8 character split between two polls");

            // truncate in place -> restart marker, then the new content
            seen.Clear();
            long before = t.LastSeq;
            game.SetLength(0); game.Flush();
            Game("fresh start\n");
            t.Poll();
            Check(seen.Count == 2 && seen[0].Marker && seen[1].Text == "fresh start", "truncated file: restart marker, then the new lines");
            Check(seen.Count == 2 && seen[0].Seq == before + 1 && seen[1].Seq == before + 2, "sequence numbers continue across a restart");
            var fresh = t.Snapshot(-1, 50, out _);
            Check(fresh.Count == 2 && fresh[0].Marker && fresh[1].Text == "fresh start", "a fresh view starts at the newest restart marker, not in the previous run");
            Check(t.Snapshot(before - 2, 50, out _).Count == 4, "a resume (after = seq) still gets the lines before the marker");
            Check(t.LastMarkerSeq == before + 1, "LastMarkerSeq = the restart marker's seq (a resume whose batch lost it gets reset)");

            // delete + recreate (new file identity), even if the new file is longer than the old offset
            seen.Clear();
            game.Dispose();
            File.Delete(log);
            var sb = new StringBuilder();
            for (int i = 0; i < 20; i++) sb.Append("recreated line ").Append(i).Append('\n');
            File.WriteAllText(log, sb.ToString());
            t.Poll();
            Check(seen.Count == 21 && seen[0].Marker && seen[1].Text == "recreated line 0", "recreated file: restart marker and everything from the top (" + seen.Count + " lines, first: " + (seen.Count > 0 ? seen[0].Text : "-") + ")");

            // ring bounds + resume
            long last = t.LastSeq;
            var all = new StringBuilder();
            for (int i = 0; i < 200; i++) all.Append("r").Append(i).Append('\n');
            Append(log, all.ToString());
            t.Poll();
            Check(t.LastSeq == last + 200, "200 appended lines are all counted");
            var tail10 = t.Snapshot(-1, 10, out bool gapA);
            Check(tail10.Count == 10 && tail10[^1].Text == "r199" && tail10[0].Text == "r190" && !gapA, "snapshot of the newest 10");
            var full = t.Snapshot(-1, 100000, out _);
            Check(full.Count == 50 && full[^1].Text == "r199", "ring holds at most its size (50)");
            var res = t.Snapshot(last + 195, 0, out bool gapB);
            Check(Texts(res).SequenceEqual(new[] { "r195", "r196", "r197", "r198", "r199" }) && !gapB, "resume after seq: exactly the missed lines, no duplicates");
            t.Snapshot(last + 5, 0, out bool gapC);
            Check(gapC, "resume from a seq the ring no longer holds reports a gap");
            Check(t.Snapshot(t.LastSeq, 0, out _).Count == 0, "resume at the newest seq: nothing");

            // a huge first read starts near the end and drops the partial first line
            string big = Path.Combine(dir, "big.txt");
            var bs = new StringBuilder();
            for (int i = 0; i < 20000; i++) bs.Append("big line number ").Append(i).Append('\n');
            File.WriteAllText(big, bs.ToString());
            using var tb = new LogTail(big, 5000, 100, initialBytes: 100 * 1024);
            tb.Poll();
            var bl = tb.Snapshot(-1, 100000, out _);
            Check(bl.Count > 1000 && bl.Count <= 5000 && bl[^1].Text == "big line number 19999" && bl[0].Text.StartsWith("big line number "), "big file: only the tail is read, first partial line dropped");
            Check(bl.Select(x => x.Text).All(x => x.StartsWith("big line number ") && int.TryParse(x[16..], out _)), "big file: every kept line is whole");

            // an endless line cannot grow memory without bound
            string endless = Path.Combine(dir, "endless.txt");
            File.WriteAllText(endless, new string('x', 600 * 1024));
            using var te = new LogTail(endless, 50, 100);
            te.Poll();
            var el = te.Snapshot(-1, 10, out _);
            Check(el.Count >= 1 && el.All(x => x.Text.Length <= LogTail.MaxLineChars + 20), "an unterminated huge line is emitted in capped pieces");
        } catch (Exception e) {
            Check(false, "exception: " + e);
        } finally {
            try { Directory.Delete(dir, true); } catch { }
        }
        Console.WriteLine(_fails == 0 ? "LOGTAIL SELFTEST OK" : $"LOGTAIL SELFTEST: {_fails} failure(s)");
        return _fails;
    }
}
