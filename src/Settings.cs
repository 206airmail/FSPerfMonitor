// Settings.cs - the tray app's settings: %APPDATA%\SVSPerfMonitor\settings.json, edited on the dashboard's Settings page
// (from this PC only). The command line never reads it, so scripts and test runners stay reproducible; in the tray app,
// options given on its command line (e.g. --lan) still win over the file for that run.
// Copyright (c) 2026 Jared Brown. MIT licence.
namespace FSPerf;

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public bool OpenDashboardOnStart { get; set; } = true;
    public bool MainLoopBreakdown { get; set; }            // main-thread sampling (pauses the game; off by default, #2)
    public string ProfileFolder { get; set; } = "";        // "" = found automatically
    public int Port { get; set; } = 8765;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

// what GET /api/settings returns: the settings, whether this viewer may change them, and the facts the page shows
public sealed record SettingsView(AppSettings Settings, bool CanEdit, string ReadOnlyReason, string DefaultProfileFolder,
    string Version, string Url);

public static class SettingsStore
{
    public static string DefaultProfileFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "FarmingSimulator2025");

    public static string? OverridePath;   // --settings <file>
    public static string FilePath => OverridePath ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SVSPerfMonitor", "settings.json");

    // a missing or unreadable file = the defaults (never a reason not to start)
    public static AppSettings Load()
    {
        try {
            if (File.Exists(FilePath))
                return System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(FilePath), FsperfJson.Default.AppSettings) ?? new();
        } catch { }
        return new AppSettings();
    }

    // write to a temp file, then replace: a crash or power loss never leaves half a file
    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(s, FsperfJson.Default.AppSettings));
        File.Move(tmp, FilePath, overwrite: true);
    }

    // null = valid; else the message the Settings page shows next to the field
    public static string? Validate(AppSettings s)
    {
        if (s.Port < 1 || s.Port > 65535) return "Enter a whole number from 1 to 65535.";
        if (s.ProfileFolder.Length > 0 && !Directory.Exists(s.ProfileFolder)) return "That folder doesn't exist.";
        return null;
    }

    // the tray app's options: the settings, then its own command line on top
    public static Options ToOptions(AppSettings s, string[] args)
    {
        var o = new Options {
            Port = s.Port,
            SampleHz = s.MainLoopBreakdown ? 250 : 0,
            ProfileDir = s.ProfileFolder.Length > 0 ? s.ProfileFolder : DefaultProfileFolder,
            NoBrowser = !s.OpenDashboardOnStart,
        };
        return Options.Parse(args, o);
    }
}
