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
            cuts = ChooseEvenly(cuts, policy.MaxMidBreaks, durationMs);
            cuts = EnforceMinAct(cuts, policy.MinActMs);
        }
        else if (policy.FallbackBreaks > 0)
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

    /// <summary>Keep at most <paramref name="max"/> cuts, each the candidate nearest to an evenly spaced target.</summary>
    private static List<long> ChooseEvenly(List<long> candidates, int max, long durationMs)
    {
        if (max <= 0) return [];
        if (candidates.Count <= max) return candidates;

        var chosen = new SortedSet<long>();
        for (int i = 1; i <= max; i++)
        {
            long target = durationMs * i / (max + 1);
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
