using TickleTV.Scheduler.Core.Media;

namespace TickleTV.Scheduler.Core.Engine;

public sealed record BreakPolicy
{
    public bool UseChapters { get; init; } = true;
    public int MaxMidBreaks { get; init; } = 3;
    /// <summary>No act may be shorter than this, so breaks never land seconds apart.</summary>
    public long MinActMs { get; init; } = 120_000;
    public bool PreRoll { get; init; } = false;
    public bool PostRoll { get; init; } = true;
    /// <summary>When there are no usable chapters, split evenly into this many breaks (0 = none).</summary>
    public int FallbackBreaks { get; init; } = 0;
    /// <summary>
    /// A first chapter between <see cref="ColdOpenMinMs"/> and <see cref="ColdOpenMaxMs"/> long is a cold open
    /// or teaser; real TV always breaks right after it. When true, that cut is kept ahead of even spacing.
    /// </summary>
    public bool ColdOpenBreak { get; init; } = true;
    public long ColdOpenMinMs { get; init; } = 60_000;
    public long ColdOpenMaxMs { get; init; } = 8 * 60_000;
}

/// <summary>A contiguous slice of the source file that airs between breaks.</summary>
public sealed record Act(long InMs, long OutMs)
{
    public long DurationMs => OutMs - InMs;
}

public static class BreakPlanner
{
    /// <summary>
    /// Splits a file into acts. Cut points come from chapter boundaries that respect the minimum act
    /// length; when there are more candidates than allowed breaks, the ones closest to even spacing win.
    /// </summary>
    public static IReadOnlyList<Act> PlanActs(long durationMs, IReadOnlyList<Chapter> chapters, BreakPolicy policy)
    {
        if (durationMs <= 0) return [];

        List<long> cuts = [];
        if (policy.UseChapters && chapters.Count > 1)
        {
            cuts = chapters
                .Skip(1)
                .Select(c => c.StartMs)
                .Where(ms => ms >= policy.MinActMs && ms <= durationMs - policy.MinActMs)
                .Distinct()
                .OrderBy(ms => ms)
                .ToList();
            long? coldOpen = policy.ColdOpenBreak && chapters.Count > 1
                && chapters[1].StartMs >= Math.Max(policy.ColdOpenMinMs, policy.MinActMs)
                && chapters[1].StartMs <= policy.ColdOpenMaxMs
                && chapters[1].StartMs <= durationMs - policy.MinActMs
                ? chapters[1].StartMs : null;
            cuts = ChooseEvenly(cuts, policy.MaxMidBreaks, durationMs, coldOpen);
            cuts = EnforceMinAct(cuts, policy.MinActMs);
        }

        // Chapters that yield no usable cut (one mark near the end, say) are as good as none: fall back.
        if (cuts.Count == 0 && policy.FallbackBreaks > 0)
        {
            int n = Math.Min(policy.FallbackBreaks, policy.MaxMidBreaks);
            for (int i = 1; i <= n; i++)
                cuts.Add(durationMs * i / (n + 1));
            cuts = cuts.Where(ms => ms >= policy.MinActMs && ms <= durationMs - policy.MinActMs).ToList();
        }

        var acts = new List<Act>();
        long cursor = 0;
        foreach (long cut in cuts)
        {
            acts.Add(new Act(cursor, cut));
            cursor = cut;
        }
        acts.Add(new Act(cursor, durationMs));
        return acts;
    }

    /// <summary>
    /// Keep at most <paramref name="max"/> cuts. A forced cut (the cold open) is kept first; the rest are the
    /// candidates nearest to targets spaced evenly across what remains after it.
    /// </summary>
    private static List<long> ChooseEvenly(List<long> candidates, int max, long durationMs, long? forced = null)
    {
        if (max <= 0) return [];
        if (candidates.Count <= max) return candidates;

        var chosen = new SortedSet<long>();
        long spanStart = 0;
        int remaining = max;
        if (forced is { } f && candidates.Contains(f))
        {
            chosen.Add(f);
            spanStart = f;
            remaining--;
        }
        for (int i = 1; i <= remaining; i++)
        {
            long target = spanStart + (durationMs - spanStart) * i / (remaining + 1);
            long? best = candidates
                .Where(c => !chosen.Contains(c))
                .OrderBy(c => Math.Abs(c - target))
                .Select(c => (long?)c)
                .FirstOrDefault();
            if (best is { } b) chosen.Add(b);
        }
        return chosen.ToList();
    }

    /// <summary>Drop any cut that would make an act shorter than the minimum.</summary>
    private static List<long> EnforceMinAct(List<long> cuts, long minActMs)
    {
        var result = new List<long>();
        long last = 0;
        foreach (long cut in cuts)
        {
            if (cut - last >= minActMs)
            {
                result.Add(cut);
                last = cut;
            }
        }
        return result;
    }
}
