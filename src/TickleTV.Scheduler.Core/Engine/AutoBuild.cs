using System.Globalization;

namespace TickleTV.Scheduler.Core.Engine;

/// <summary>The nightly automatic build: whether it is on, what time it runs, and how many days it writes.</summary>
public sealed class AutoBuildConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Local time of day, 24-hour, like 03:00.</summary>
    public string Time { get; set; } = "03:00";
    /// <summary>How many days each automatic build writes. Two means a missed night does not go dark.</summary>
    public int Days { get; set; } = 2;

    public TimeOnly TimeOfDay => AutoBuild.ParseTime(Time);
}

/// <summary>When the nightly build is due. Pure so it can be tested; the app's background worker calls it once a minute.</summary>
public static class AutoBuild
{
    public static readonly TimeOnly DefaultTime = new(3, 0);

    public static TimeOnly ParseTime(string? text) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm", "h:mm tt", "h tt"], CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly t)
            ? t : DefaultTime;

    /// <summary>
    /// A build is due when it is on, today's build has not run yet, and the clock has passed the build time.
    /// A computer that was off at build time catches up as soon as the app runs.
    /// </summary>
    public static bool IsDue(AutoBuildConfig cfg, DateTime now, DateOnly? lastRunDay)
    {
        if (!cfg.Enabled) return false;
        DateOnly today = DateOnly.FromDateTime(now);
        if (lastRunDay is { } last && last >= today) return false;
        return TimeOnly.FromDateTime(now) >= cfg.TimeOfDay;
    }

    /// <summary>When the next automatic build will start, or null when it is switched off.</summary>
    public static DateTime? NextRun(AutoBuildConfig cfg, DateTime now, DateOnly? lastRunDay)
    {
        if (!cfg.Enabled) return null;
        if (IsDue(cfg, now, lastRunDay)) return now;
        DateOnly today = DateOnly.FromDateTime(now);
        bool ranToday = lastRunDay is { } last && last >= today;
        DateOnly day = !ranToday && TimeOnly.FromDateTime(now) < cfg.TimeOfDay ? today : today.AddDays(1);
        return day.ToDateTime(cfg.TimeOfDay);
    }

    public static int ClampDays(int days) => Math.Clamp(days, 1, BuildOptions.MaxDays);
}
