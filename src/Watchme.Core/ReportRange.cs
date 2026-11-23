using System.Globalization;

namespace Watchme.Core;

/// <summary>
/// Reads the day or range of days a command is about: <c>today</c>, <c>yesterday</c>, a date,
/// <c>7d</c> (the last 7 days, today included), <c>week</c>, <c>last-week</c>, <c>month</c>,
/// <c>last-month</c>, or <c>FROM..TO</c> where either side is a date, today or yesterday
/// (an empty TO means today).
/// </summary>
public static class ReportRange
{
    public const string Forms =
        "today, yesterday, YYYY-MM-DD, 7d (last N days), week, last-week, month, last-month or FROM..TO";

    /// <summary>The longest range accepted, the same limit the dashboard has.</summary>
    public const int MaxDays = 366;

    public static bool TryParse(string? arg, DateOnly today, out DateOnly from, out DateOnly to)
    {
        from = to = today;
        var s = (arg ?? "today").Trim().ToLowerInvariant();

        var dots = s.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            var left = s[..dots];
            var right = s[(dots + 2)..];
            if (!TryDay(left, today, out from))
                return false;
            if (right.Length == 0)
                to = today;
            else if (!TryDay(right, today, out to))
                return false;
            if (to < from)
                (from, to) = (to, from);
            return to.DayNumber - from.DayNumber < MaxDays;
        }

        if (s.EndsWith('d') && int.TryParse(s[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            if (n is < 1 or > MaxDays)
                return false;
            from = today.AddDays(-(n - 1));
            return true;
        }

        switch (s)
        {
            case "week":
                from = Monday(today);
                return true;
            case "last-week" or "lastweek":
                from = Monday(today).AddDays(-7);
                to = from.AddDays(6);
                return true;
            case "month":
                from = new DateOnly(today.Year, today.Month, 1);
                return true;
            case "last-month" or "lastmonth":
                to = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
                from = new DateOnly(to.Year, to.Month, 1);
                return true;
        }

        if (!TryDay(s, today, out from))
            return false;
        to = from;
        return true;
    }

    /// <summary>One day: today, yesterday or YYYY-MM-DD.</summary>
    public static bool TryDay(string? s, DateOnly today, out DateOnly day)
    {
        day = today;
        switch (s?.Trim().ToLowerInvariant())
        {
            case "today":
                return true;
            case "yesterday":
                day = today.AddDays(-1);
                return true;
            default:
                return DateOnly.TryParseExact(s?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
        }
    }

    private static DateOnly Monday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}
