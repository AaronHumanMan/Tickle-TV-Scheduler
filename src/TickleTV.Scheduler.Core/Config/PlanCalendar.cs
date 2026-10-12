using System.Globalization;

namespace TickleTV.Scheduler.Core.Config;

/// <summary>The holidays the calendar knows by name, with their dates worked out for any year.</summary>
public static class Holidays
{
    public sealed record Holiday(string Key, string Name, Func<int, DateOnly> On);

    public static readonly IReadOnlyList<Holiday> All =
    [
        new("new-years-day", "New Year's Day", y => new DateOnly(y, 1, 1)),
        new("valentines-day", "Valentine's Day", y => new DateOnly(y, 2, 14)),
        new("st-patricks-day", "St Patrick's Day", y => new DateOnly(y, 3, 17)),
        new("easter", "Easter Sunday", Easter),
        new("independence-day", "Independence Day", y => new DateOnly(y, 7, 4)),
        new("halloween", "Halloween", y => new DateOnly(y, 10, 31)),
        new("thanksgiving", "Thanksgiving", y => NthWeekday(y, 11, DayOfWeek.Thursday, 4)),
        new("christmas-eve", "Christmas Eve", y => new DateOnly(y, 12, 24)),
        new("christmas", "Christmas Day", y => new DateOnly(y, 12, 25)),
    ];

    public static Holiday? Find(string? key) =>
        key is null ? null : All.FirstOrDefault(h => string.Equals(h.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Gregorian Easter (Anonymous/Meeus algorithm).</summary>
    public static DateOnly Easter(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4;
        int f = (b + 8) / 25, g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }

    private static DateOnly NthWeekday(int year, int month, DayOfWeek weekday, int n)
    {
        var first = new DateOnly(year, month, 1);
        int offset = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 7 * (n - 1));
    }
}

/// <summary>
/// Which day plan a channel follows on a given date. Rules are checked from most to least specific: a date
/// set by hand on the calendar, then a holiday or a yearly date range, then a day-of-the-week rule, then the
/// channel's default plan. Among holiday and range rules the shortest span wins, then the one listed first.
/// </summary>
public static class PlanCalendar
{
    public static readonly string[] DayKeys = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];

    public static DayPlan PlanFor(ChannelConfig ch, DateOnly date)
    {
        CalendarRule? rule = RuleFor(ch, date);
        return (rule is null ? null : ch.FindPlan(rule.Plan)) ?? ch.Default;
    }

    public static string PlanNameFor(ChannelConfig ch, DateOnly date) => PlanFor(ch, date).Name;

    /// <summary>The rule that decides this date, or null when the default plan applies.</summary>
    public static CalendarRule? RuleFor(ChannelConfig ch, DateOnly date)
    {
        CalendarRule? pinned = ch.Calendar.FirstOrDefault(r => r.Kind == RuleKind.Dates && r.ParsedDates.Contains(date));
        if (pinned is not null) return pinned;

        CalendarRule? seasonal = ch.Calendar
            .Where(r => r.Kind is RuleKind.Holiday or RuleKind.Range && r.Covers(date))
            .OrderBy(r => r.SpanDays(date.Year))
            .FirstOrDefault();
        if (seasonal is not null) return seasonal;

        return ch.Calendar.FirstOrDefault(r => r.Kind == RuleKind.Weekdays && r.Covers(date));
    }

    /// <summary>The plan used on each day of a month, for the calendar page.</summary>
    public static IReadOnlyList<(DateOnly Date, DayPlan Plan, CalendarRule? Rule)> Month(ChannelConfig ch, int year, int month)
    {
        var days = new List<(DateOnly, DayPlan, CalendarRule?)>();
        for (var d = new DateOnly(year, month, 1); d.Month == month; d = d.AddDays(1))
        {
            CalendarRule? rule = RuleFor(ch, d);
            days.Add((d, (rule is null ? null : ch.FindPlan(rule.Plan)) ?? ch.Default, rule));
        }
        return days;
    }

    /// <summary>Pins a date to a plan on the calendar page (null plan: let the rules decide again).</summary>
    public static void PinDate(ChannelConfig ch, DateOnly date, string? plan)
    {
        string text = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (CalendarRule r in ch.Calendar.Where(r => r.Kind == RuleKind.Dates))
            r.Dates.RemoveAll(d => string.Equals(d.Trim(), text, StringComparison.Ordinal));
        ch.Calendar.RemoveAll(r => r.Kind == RuleKind.Dates && r.Dates.Count == 0);
        if (plan is null) return;

        CalendarRule? target = ch.Calendar.FirstOrDefault(r => r.Kind == RuleKind.Dates && string.Equals(r.Plan, plan, StringComparison.OrdinalIgnoreCase));
        if (target is null) ch.Calendar.Add(target = new CalendarRule { Plan = plan });
        target.Dates.Add(text);
        target.Dates.Sort(StringComparer.Ordinal);
    }

    /// <summary>"Saturdays and Sundays", "December 18 to 26 every year", "the week leading up to Christmas Day".</summary>
    public static string Describe(CalendarRule r) => r.Kind switch
    {
        RuleKind.Weekdays => Join(r.Days.Select(d => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(d.Trim().ToLowerInvariant()) + "s")),
        RuleKind.Holiday when Holidays.Find(r.Holiday) is { } h => SpanWords(r.DaysBefore ?? 0, r.DaysAfter ?? 0, h.Name),
        RuleKind.Holiday => $"an unknown holiday '{r.Holiday}'",
        RuleKind.Range => $"{MonthDayWords(r.From)} to {MonthDayWords(r.To)} every year",
        RuleKind.Dates => r.Dates.Count == 1 ? DateWords(r.Dates[0]) : $"{r.Dates.Count} days picked on the calendar",
        _ => "nothing",
    };

    public static string SpanWords(int before, int after, string holiday) => (before, after) switch
    {
        (0, 0) => holiday,
        (6, 0) => $"the week leading up to {holiday}",
        (3, 3) => $"the week around {holiday}",
        (_, 0) => $"{before} days before {holiday} through the day itself",
        (0, _) => $"{holiday} and the {after} days after",
        _ => $"{before} days before to {after} days after {holiday}",
    };

    private static string Join(IEnumerable<string> words)
    {
        var list = words.ToList();
        return list.Count switch
        {
            0 => "no days",
            1 => list[0],
            2 => $"{list[0]} and {list[1]}",
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }

    private static string MonthDayWords(string? monthDay) =>
        CalendarRule.ParseMonthDay(monthDay) is { } md ? $"{CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(md.Month)} {md.Day}" : "?";

    private static string DateWords(string date) =>
        DateOnly.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d)
            ? d.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)
            : date;
}
