using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watchme.Core;

/// <summary>User settings, stored as JSON in the data directory. Created with defaults on first run.</summary>
public sealed class WatchmeConfig
{
    /// <summary>No keyboard or mouse input for this long marks you as idle.</summary>
    public int IdleThresholdSeconds { get; set; } = 300;

    /// <summary>A window title must stay the same this long before it is recorded (filters "Copying 45%" noise).</summary>
    public int TitleStableSeconds { get; set; } = 2;

    public int ProcessPollSeconds { get; set; } = 5;

    /// <summary>Apps whose window titles are never stored (only that they had focus).</summary>
    public List<string> RedactTitleApps { get; set; } =
        ["KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe"];

    /// <summary>Any title containing one of these (case-insensitive) is not stored.</summary>
    public List<string> RedactTitlePatterns { get; set; } =
        ["InPrivate", "Incognito", "Private Browsing"];

    /// <summary>Helper processes left out of process start/exit events.</summary>
    public List<string> IgnoreProcesses { get; set; } =
    [
        "conhost.exe", "dllhost.exe", "RuntimeBroker.exe", "backgroundTaskHost.exe",
        "SearchProtocolHost.exe", "SearchFilterHost.exe", "WerFault.exe", "smartscreen.exe",
        "taskhostw.exe", "WmiPrvSE.exe", "audiodg.exe", "consent.exe",
    ];

    /// <summary>Read plays, tag edits and album additions from Aurora's own databases.</summary>
    public bool AuroraEnabled { get; set; } = true;

    /// <summary>Aurora's data folder; null means %APPDATA%\com.soundtrackgeek.aurora.</summary>
    public string? AuroraDataDir { get; set; }

    /// <summary>Read the tracks you play and rate in Aurora NG from its listening history and rating log.</summary>
    public bool AuroraNgEnabled { get; set; } = true;

    /// <summary>Aurora NG's data folder; null means %LOCALAPPDATA%\aurora_ng.</summary>
    public string? AuroraNgDataDir { get; set; }

    /// <summary>Read rating changes (from any app) from Music Library's catalog.</summary>
    public bool MusicLibraryEnabled { get; set; } = true;

    /// <summary>Music Library's catalog; null means %APPDATA%\com.local.musiclibrary\music-library.sqlite3.</summary>
    public string? MusicLibraryDb { get; set; }

    /// <summary>Read file operations from Total Commander's log file (enable it under Configuration → Log file).</summary>
    public bool TotalCommanderEnabled { get; set; } = true;

    /// <summary>Path to wincmd.ini, where the log file location is read from; null means %APPDATA%\GHISLER\wincmd.ini.</summary>
    public string? TotalCommanderIni { get; set; }

    /// <summary>Read T3 Code runs (prompts, models, timing) for Claude Code, Codex, Antigravity...</summary>
    public bool T3Enabled { get; set; } = true;

    /// <summary>T3's state database; null means %USERPROFILE%\.t3\userdata\statev2.sqlite.</summary>
    public string? T3StateDb { get; set; }

    /// <summary>Read commits, checkouts, pulls, pushes... from the reflogs of repos under <see cref="GitRoots"/>.</summary>
    public bool GitEnabled { get; set; } = true;

    /// <summary>
    /// Folders holding your repos (looked for up to two levels down). New folders directly inside
    /// them are recorded too. Environment variables are expanded; missing folders are skipped.
    /// </summary>
    public List<string> GitRoots { get; set; } =
        [@"C:\_code", @"C:\code", @"%USERPROFILE%\source\repos", @"%USERPROFILE%\code"];

    /// <summary>Read the commands you run in PowerShell, logged by the hook from <c>watchme powershell install</c>.</summary>
    public bool PowerShellEnabled { get; set; } = true;

    /// <summary>On first run, scrapers import this many days of history from apps.</summary>
    public int ScrapeBackfillDays { get; set; } = 7;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Loads the config, writing the defaults to disk if the file does not exist yet.</summary>
    public static WatchmeConfig Load(string path)
    {
        if (File.Exists(path))
            return JsonSerializer.Deserialize<WatchmeConfig>(File.ReadAllText(path), JsonOptions) ?? new();

        var config = new WatchmeConfig();
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        return config;
    }

    public bool ShouldRedactTitle(string? app, string? title)
    {
        if (string.IsNullOrEmpty(title))
            return false;
        if (app is not null && RedactTitleApps.Contains(app, StringComparer.OrdinalIgnoreCase))
            return true;
        return RedactTitlePatterns.Any(p => title.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
}
