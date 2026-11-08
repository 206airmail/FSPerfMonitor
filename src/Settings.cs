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
    public bool StartWithWindows { get; set; }              // #7: Run entry, or the task's logon trigger with full permissions
    public bool RunWithFullPermissions { get; set; }        // #7: the scheduled task (highest privileges)
    public bool WelcomeShown { get; set; }                  // #8: the first start opens /welcome instead of the dashboard
    public string UserCopy { get; set; } = "";              // the user's own starter (not the protected copy): the Run entry's target

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    // JSON can put null where C# says "never null" (a hand-edited file, a crafted request): make it the default
    public AppSettings Normalized()
    {
        ProfileFolder ??= "";
        UserCopy ??= "";
        return this;
    }
}

// what GET /api/settings returns: the settings, whether this viewer may change them, and the facts the page shows
// FrameTimes: "ready" | "signout" (in the group, sign out and back in) | "setup" (not set up) | "missing" (no PresentMon)
public sealed record SettingsView(AppSettings Settings, bool CanEdit, string ReadOnlyReason, string DefaultProfileFolder,
    string Version, string Url, string Token, string FrameTimes, bool Elevated);

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
                return (System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(FilePath), FsperfJson.Default.AppSettings) ?? new()).Normalized();
        } catch { }
        return new AppSettings();
    }

    // write to a temp file of its own, then replace: a crash or power loss never leaves half a file. Callers serialize saves.
    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string tmp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try {
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(s, FsperfJson.Default.AppSettings));
            File.Move(tmp, FilePath, overwrite: true);
        } finally {
            try { File.Delete(tmp); } catch { }   // gone already after a successful move
        }
    }

    // null = valid; else the message the Settings page shows next to the field
    public static string? Validate(AppSettings s)
    {
        if (s.Port < 1 || s.Port > 65535) return "Enter a whole number from 1 to 65535.";
        if (s.ProfileFolder.Length > 0) {
            // a folder on this PC only: a network or device path (\\server\share, \\?\, \\.\) would make even the existence
            // check below connect to that server (and Windows may send the user's login to it)
            string f = s.ProfileFolder;
            bool localDrive = f.Length >= 3 && char.IsAsciiLetter(f[0]) && f[1] == ':' && (f[2] == '\\' || f[2] == '/');
            if (!localDrive) return "Choose a folder on this PC, like C:\\Users\\you\\Documents\\My Games\\FarmingSimulator2025.";
            try { if (new DriveInfo(f[..1]).DriveType == DriveType.Network) return "Choose a folder on this PC, not on a network drive."; } catch { }
            if (!Directory.Exists(f)) return "That folder doesn't exist.";
        }
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
        // a start by Windows (logon) or a hand-over to the full-permissions copy never opens the browser
        if (args.Contains("--autostart")) o.NoBrowser = true;
        return Options.Parse(args, o);
    }
}
