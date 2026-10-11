namespace TickleTV.Scheduler.Core.Engine;

/// <summary>
/// Chooses clips to fill a break budget as closely as possible. Clips may repeat within a break,
/// up to <see cref="MaxRepeatsPerBreak"/> times each, but never back to back. Whatever is left is
/// reported as <see cref="FillResult.RemainderMs"/> for the caller to cover with a slate.
/// </summary>
public sealed class BreakFiller
{
    public int MaxRepeatsPerBreak { get; init; } = 2;

    /// <summary>Resolution of the knapsack in milliseconds; clips are rounded down to it.</summary>
    public int ResolutionMs { get; init; } = 100;

    private readonly IReadOnlyList<FillerClip> _clips;
    private readonly Dictionary<string, int> _plays = new();

    public BreakFiller(IReadOnlyList<FillerClip> clips)
    {
        _clips = clips.Where(c => c.DurationMs > 0).ToList();
    }

    public bool HasClips => _clips.Count > 0;

    public FillResult Fill(long budgetMs)
    {
        if (budgetMs <= 0 || _clips.Count == 0) return new FillResult([], budgetMs);

        List<FillerClip> chosen = Knapsack(budgetMs);
        List<FillerClip> ordered = Arrange(chosen);
        long used = ordered.Sum(c => c.DurationMs);
        foreach (FillerClip c in ordered)
            _plays[c.Path] = _plays.GetValueOrDefault(c.Path) + 1;
        return new FillResult(ordered, budgetMs - used);
    }

    /// <summary>
    /// Bounded knapsack over budget units. Among fills with the same leftover, prefer the one that
    /// uses clips played least so far, so rotation spreads across the library.
    /// </summary>
    private List<FillerClip> Knapsack(long budgetMs)
    {
        int units = (int)(budgetMs / ResolutionMs);
        var items = new List<(FillerClip clip, int size)>();
        foreach (FillerClip clip in _clips)
        {
            int size = (int)(clip.DurationMs / ResolutionMs);
            if (size <= 0 || size > units) continue;
            // Exact duration must also fit the real budget, not just the rounded one.
            for (int r = 0; r < MaxRepeatsPerBreak; r++) items.Add((clip, size));
        }
        if (items.Count == 0) return [];

        // best[u] = (reachable, cost) where cost is the sum of prior play counts; parent pointers rebuild the pick.
        var reachable = new bool[units + 1];
        var cost = new long[units + 1];
        var parent = new int[units + 1];
        var parentItem = new int[units + 1];
        reachable[0] = true;
        Array.Fill(parent, -1);

        // 0/1 over the expanded item list; iterate units downward so each copy is used at most once.
        for (int i = 0; i < items.Count; i++)
        {
            (FillerClip clip, int size) = items[i];
            long itemCost = _plays.GetValueOrDefault(clip.Path) + 1;
            for (int u = units; u >= size; u--)
            {
                if (!reachable[u - size]) continue;
                long candidate = cost[u - size] + itemCost;
                if (!reachable[u] || candidate < cost[u])
                {
                    reachable[u] = true;
                    cost[u] = candidate;
                    parent[u] = u - size;
                    parentItem[u] = i;
                }
            }
        }

        int best = units;
        while (best > 0 && !reachable[best]) best--;

        // Reconstruct. Guard against the real durations exceeding the real budget after rounding.
        var picks = new List<FillerClip>();
        for (int u = best; u > 0; u = parent[u])
            picks.Add(items[parentItem[u]].clip);

        while (picks.Sum(c => c.DurationMs) > budgetMs && picks.Count > 0)
            picks.RemoveAt(picks.Count - 1);
        return picks;
    }

    /// <summary>
    /// Order the picks so no clip plays twice in a row: always take the clip with the most copies
    /// left that differs from the previous one, longest first on ties, so duplicates spread out.
    /// </summary>
    private static List<FillerClip> Arrange(List<FillerClip> picks)
    {
        var counts = picks.GroupBy(c => c.Path).ToDictionary(g => g.Key, g => (clip: g.First(), left: g.Count()));
        var result = new List<FillerClip>();
        string? last = null;
        while (counts.Count > 0)
        {
            var next = counts.Values
                .Where(v => v.clip.Path != last)
                .OrderByDescending(v => v.left)
                .ThenByDescending(v => v.clip.DurationMs)
                .Select(v => v.clip)
                .FirstOrDefault()
                ?? counts.Values.First().clip; // only copies of the last clip remain; accept the repeat

            result.Add(next);
            (FillerClip clip, int left) = counts[next.Path];
            if (left == 1) counts.Remove(next.Path); else counts[next.Path] = (clip, left - 1);
            last = next.Path;
        }
        return result;
    }
}

public sealed record FillResult(IReadOnlyList<FillerClip> Clips, long RemainderMs);
