using TickleTV.Scheduler.Core.Config;

namespace TickleTV.Scheduler.Core.Engine;

/// <summary>
/// Plain-language notes about a block of time that holds several shows: how long the block is and whether
/// all of its shows can play in one day. Used by the schedule editor and the build warnings alike.
/// </summary>
public static class BlockAdvisor
{
    /// <summary>From this slot's start to the next slot's start, wrapping past midnight; a lone slot is a whole day.</summary>
    public static TimeSpan BlockLength(ChannelConfig ch, SlotConfig slot)
    {
        List<SlotConfig> plan = ch.PlanOf(slot)?.Slots ?? ch.Schedule;
        var slots = plan.Where(s => TimeOnly.TryParse(s.Time, out _)).OrderBy(s => s.StartTime).ToList();
        int i = slots.IndexOf(slot);
        if (i < 0 || slots.Count == 1) return TimeSpan.FromHours(24);
        TimeSpan len = slots[(i + 1) % slots.Count].StartTime.ToTimeSpan() - slot.StartTime.ToTimeSpan();
        return len <= TimeSpan.Zero ? len + TimeSpan.FromHours(24) : len;
    }

    /// <summary>
    /// A note when the block's shows cannot all play in one day, or null when they can. Lengths are each show's
    /// average episode padded the way the slot pads, so a 45-minute show in an hour-padded slot counts as an hour.
    /// </summary>
    public static string? Advice(ChannelConfig ch, SlotConfig slot, Func<string, long?> averageMs)
    {
        IReadOnlyList<string> shows = slot.ShowList;
        if (shows.Count < 2 || !TimeOnly.TryParse(slot.Time, out _)) return null;

        long blockMs = (long)BlockLength(ch, slot).TotalMilliseconds;
        PaddingPolicy pad = slot.PaddingPolicy(ch.Defaults);
        var anchor = new DateTimeOffset(2000, 1, 3, 0, 0, 0, TimeSpan.Zero).Add(slot.StartTime.ToTimeSpan());

        var fits = new List<(string Show, long Avg, long Padded)>();
        foreach (string show in shows.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (averageMs(show) is not { } avg || avg <= 0) continue;
            long padded = (long)(Padding.SlotEnd(anchor, avg, pad) - anchor).TotalMilliseconds;
            fits.Add((show, avg, padded));
        }
        if (fits.Count < 2 || fits.Sum(f => f.Padded) <= blockMs) return null;

        int room = 0;
        long used = 0;
        foreach (var f in fits.OrderBy(f => f.Padded))
        {
            if (used + f.Padded > blockMs) break;
            used += f.Padded;
            room++;
        }

        string lengths = string.Join(", ", fits.Select(f => $"{f.Show} is about {Minutes(f.Avg)} min"));
        string block = BlockWords(blockMs);
        if (room == 0)
            return $"This {block} block is shorter than any of its shows ({lengths}), so each day one episode will run past the end of the block.";
        if (room == 1)
            return $"Only one show will play in this {block} block each day ({lengths}). The {fits.Count} shows take turns, one per day. That is fine, just so you know.";
        return $"This {block} block has room for about {room} episodes a day but holds {fits.Count} shows ({lengths}). The shows take turns across days.";
    }

    private static long Minutes(long ms) => (ms + 30_000) / 60_000;

    private static string BlockWords(long ms)
    {
        long min = Minutes(ms);
        if (min % 60 == 0) return min == 60 ? "1-hour" : $"{min / 60}-hour";
        return $"{min}-minute";
    }
}
