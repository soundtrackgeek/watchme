using Watchme.Core;

namespace Watchme.Agent;

/// <summary>What the tray icon shows: recording or paused, and whether a source needs a look.</summary>
internal sealed record TrayStatus(PauseState.Pause? Pause, IReadOnlyList<AgentHealth.SourceRow> Problems)
{
    public enum Alert { None, Warning, Failing }

    /// <summary>Windows cuts notification-area tooltips off at 127 characters.</summary>
    public const int MaxTooltip = 127;

    public static readonly TrayStatus Starting = new(null, []);

    public static TrayStatus From(AgentHealth.Report report) => new(report.Pause, report.Problems.ToList());

    /// <summary>The same status with the pause read again, for when only the pause changed.</summary>
    public TrayStatus WithPause(PauseState.Pause? pause) => this with { Pause = pause };

    public bool Paused => Pause is not null;

    /// <summary>A failing source turns the badge red; one that stalled or warns, amber.</summary>
    public Alert Badge =>
        Problems.Any(p => p.Level == AgentHealth.Level.Failing) ? Alert.Failing
        : Problems.Count > 0 ? Alert.Warning
        : Alert.None;

    /// <summary>"Recording" or "Paused until 14:30 (12 min left)".</summary>
    public string Headline(long now) =>
        Pause is { } p ? $"Paused until {AgentHealth.When(p.Until, now)} ({AgentHealth.Left(Math.Max(0, p.Until - now))} left)" : "Recording";

    public string ProblemsLine => Problems.Count == 1 ? "1 source needs a look" : $"{Problems.Count} sources need a look";

    public string Tooltip(long now)
    {
        var text = $"Watchme: {Headline(now)}";
        if (Problems.Count > 0)
            text += $"\n{ProblemsLine}";
        return text.Length <= MaxTooltip ? text : text[..(MaxTooltip - 1)] + "…";
    }

    /// <summary>A menu line for a problem source, short enough for a menu ("Edge: failing since 14:02: …").</summary>
    public static string MenuLine(AgentHealth.SourceRow row, int max = 80)
    {
        var text = row.Message is null ? row.Name : $"{row.Name}: {row.Message}";
        text = text.ReplaceLineEndings(" ");
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
