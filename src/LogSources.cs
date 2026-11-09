// LogSources.cs - the logs the log page can show (#15): the game's log.txt and FSPluginHost's logs, from its documented
// runtime files (FSPluginHost docs/ABI.md "Runtime files"): <profile>\fsp\fsp.log (host), <profile>\fsp\fs25plugins.log
// (loader), <profile>\fsp\<Name>\<Name>.log (a plugin's log). Plugin names come from fsp.status.xml and must be plain
// names: a name never becomes a path of its own choosing. Copyright (c) 2026 Jared Brown. MIT licence.
using System.Text.RegularExpressions;

namespace FSPerf;

public sealed record LogSource(string Id, string Label, string Path);

public static class LogSources
{
    public const int Max = 16;   // the game, the host, the loader and up to 13 plugins
    public const string Game = "game";
    private static readonly Regex PluginName = new(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]{0,63}$", RegexOptions.CultureInvariant);

    // ids: "game", "fsp", "loader", "plugin:<Name>"
    public static bool ValidId(string id) =>
        id is Game or "fsp" or "loader" || (id.StartsWith("plugin:", StringComparison.Ordinal) && ValidPluginName(id[7..]));

    public static bool ValidPluginName(string name) => PluginName.IsMatch(name) && !name.Contains("..");

    // the label a page shows for an id (the relay gets ids from sharers and makes its own labels)
    public static string LabelFor(string id) => id switch {
        Game => "Game log",
        "fsp" => "FSPluginHost",
        "loader" => "Plugin loader",
        _ => id.StartsWith("plugin:", StringComparison.Ordinal) ? id[7..] : id,
    };

    // the game's log always (it may not exist yet); FSPluginHost's when the files are there
    public static List<LogSource> Find(string profileDir, FspStatusInfo? fsp)
    {
        var list = new List<LogSource> { new(Game, LabelFor(Game), System.IO.Path.Combine(profileDir, "log.txt")) };
        string dir = System.IO.Path.Combine(profileDir, "fsp");
        void Add(string id, string file) { if (list.Count < Max && File.Exists(file)) list.Add(new LogSource(id, LabelFor(id), file)); }
        Add("fsp", System.IO.Path.Combine(dir, "fsp.log"));
        Add("loader", System.IO.Path.Combine(dir, "fs25plugins.log"));
        foreach (var p in (fsp?.Plugins ?? new()).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) {
            if (!ValidPluginName(p.Name)) continue;
            Add("plugin:" + p.Name, System.IO.Path.Combine(dir, p.Name, p.Name + ".log"));
        }
        return list;
    }
}
