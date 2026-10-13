using System.Collections;

namespace TickleTV.Scheduler.Core.Engine;

/// <summary>
/// Chooses clips to fill a break budget as closely as possible. Clips may repeat within a break,
/// up to <see cref="MaxRepeatsPerBreak"/> times each, but never back to back. Whatever is left is
/// reported as <see cref="FillResult.RemainderMs"/> for the caller to cover with a slate.
/// </summary>
public sealed class BreakFiller
{
    public int MaxRepeatsPerBreak { get; init; } = 2;

    /// <summary>Resolution of the knapsack in milliseconds. Clips are rounded up to it, so a fill never overshoots.</summary>
    public int ResolutionMs { get; init; } = 100;

    /// <summary>Extra cost for using a clip a second time in the same break, so variety wins when budgets tie.</summary>
    public int RepeatPenalty { get; init; } = 10;

    /// <summary>
    /// A slate shorter than this makes ffmpeg fail (Next logged exit code -22 on a 256 ms slate), so a fill
    /// leaves either nothing or at least this much. When the leftover would be smaller, the shortest pick is dropped.
    /// </summary>
    public long MinSlateMs { get; init; } = 2_000;

    /// <summary>
    /// How many different clips one break considers. The knapsack table grows with clips times budget, so handing
    /// it a whole library of twenty thousand commercials cost gigabytes per break; a few hundred of the least-played
    /// clips, chosen at random among equals, fill a break just as exactly.
    /// </summary>
    public int MaxCandidates { get; init; } = 300;

    /// <summary>Decides which clips make the cut when there are more than <see cref="MaxCandidates"/>. Null uses the shared random source.</summary>
    public Random? Random { get; init; }

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

    /// <summary>The clips this break may draw on: all of them, or the least-played <see cref="MaxCandidates"/> with ties broken at random.</summary>
    private IReadOnlyList<FillerClip> Pool()
    {
        if (_clips.Count <= MaxCandidates) return _clips;
        Random rng = Random ?? System.Random.Shared;
        return _clips
            .Select(c => (clip: c, plays: _plays.GetValueOrDefault(c.Path), key: rng.Next()))
            .OrderBy(t => t.plays).ThenBy(t => t.key)
            .Take(MaxCandidates)
            .Select(t => t.clip)
            .ToList();
    }

    /// <summary>
    /// 0/1 knapsack over an expanded item list (one entry per allowed repeat), maximizing filled time.
    /// Among fills with the same leftover, the lowest cost wins: cost grows with prior plays and with
    /// repeats inside this break, so rotation spreads across the library. Only the previous row of costs is
    /// kept; the choices are kept as one bit per cell so the exact pick can be read back.
    /// </summary>
    private List<FillerClip> Knapsack(long budgetMs)
    {
        int units = (int)(budgetMs / ResolutionMs);
        var items = new List<(FillerClip clip, int size, long cost)>();
        foreach (FillerClip clip in Pool())
        {
            int size = (int)((clip.DurationMs + ResolutionMs - 1) / ResolutionMs); // round up: never overshoot
            if (size <= 0 || size > units) continue;
            // Cost favours typical 30-second spots: a 2-minute clip costs far more than four 30-second ones,
            // so a break fills with several commercials rather than one long one. Prior plays raise the cost too.
            double seconds = clip.DurationMs / 1000.0;
            long lengthCost = (long)Math.Round(100 * (1 + Math.Pow(seconds / 30.0, 2)) / 2);
            long baseCost = (_plays.GetValueOrDefault(clip.Path) + 1) * lengthCost;
            for (int r = 0; r < MaxRepeatsPerBreak; r++)
                items.Add((clip, size, baseCost + r * RepeatPenalty * lengthCost));
        }
        if (items.Count == 0) return [];

        int n = items.Count;
        int width = units + 1;
        var reachPrev = new bool[width];
        var reachCur = new bool[width];
        var costPrev = new long[width];
        var costCur = new long[width];
        // take[i * width + u]: item i was used to reach u from the first i items.
        var take = new BitArray((n + 1) * width);
        reachPrev[0] = true;

        for (int i = 1; i <= n; i++)
        {
            (_, int size, long itemCost) = items[i - 1];
            int row = i * width;
            for (int u = 0; u < width; u++)
            {
                bool reached = reachPrev[u];
                long best = reached ? costPrev[u] : 0;
                if (u >= size && reachPrev[u - size])
                {
                    long candidate = costPrev[u - size] + itemCost;
                    if (!reached || candidate < best)
                    {
                        reached = true;
                        best = candidate;
                        take[row + u] = true;
                    }
                }
                reachCur[u] = reached;
                costCur[u] = best;
            }
            (reachPrev, reachCur) = (reachCur, reachPrev);
            (costPrev, costCur) = (costCur, costPrev);
        }

        for (int target = units; target >= 0; target--)
        {
            if (!reachPrev[target]) continue;
            List<FillerClip> picks = Reconstruct(items, take, width, n, target);
            long leftover = budgetMs - picks.Sum(c => c.DurationMs);
            if (leftover == 0 || leftover >= MinSlateMs) return picks;
            // A sliver of slate would make ffmpeg fail; try the next fuller-but-roomier target.
        }
        return [];
    }

    private static List<FillerClip> Reconstruct(List<(FillerClip clip, int size, long cost)> items, BitArray take, int width, int n, int target)
    {
        var picks = new List<FillerClip>();
        for (int i = n, u = target; i > 0 && u > 0; i--)
        {
            if (!take[i * width + u]) continue;
            picks.Add(items[i - 1].clip);
            u -= items[i - 1].size;
        }
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
