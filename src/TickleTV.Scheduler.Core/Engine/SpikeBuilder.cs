using TickleTV.Scheduler.Core.Guide;
using TickleTV.Scheduler.Core.Media;
using TickleTV.Scheduler.Core.Playout;

namespace TickleTV.Scheduler.Core.Engine;

/// <summary>A short clip (ad, bumper, filler) available to fill breaks.</summary>
public sealed record FillerClip(string Path, long DurationMs);

/// <summary>Bumpers that bracket a break: one before the ads, one after. Either list may be empty.</summary>
public sealed record BumperSet
{
    public IReadOnlyList<FillerClip> Before { get; init; } = [];
    public IReadOnlyList<FillerClip> After { get; init; } = [];
    /// <summary>Bumpers are skipped when they would leave less than this for ads.</summary>
    public long MinAdMs { get; init; } = 15_000;
}

public sealed record SpikeRequest
{
    public required MediaProbe Episode { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset Start { get; init; }
    public IReadOnlyList<FillerClip> Ads { get; init; } = [];
    public BreakPolicy BreakPolicy { get; init; } = new();
    public PaddingPolicy PaddingPolicy { get; init; } = new();
    public int MaxRepeatsPerBreak { get; init; } = 2;
    public BumperSet Bumpers { get; init; } = new();
    /// <summary>
    /// A clip that covers whatever ads and bumpers cannot: the leftover plays from its start, looping if the gap
    /// is longer than the clip. Without one, gaps are black video with silence.
    /// </summary>
    public FillerClip? Filler { get; init; }
    /// <summary>When set, the slot ends here instead of at the padding boundary (used to hold a grid time).</summary>
    public DateTimeOffset? SlotEnd { get; init; }
}

public sealed record SpikeResult
{
    public required Playout.Playout Playout { get; init; }
    public required GuideProgramme Programme { get; init; }
    public required IReadOnlyList<Act> Acts { get; init; }
    public required long PadMs { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Phase 0 spike: one episode, split at chapters, breaks filled from a folder of clips,
/// padded to a wall-clock boundary. Proves in/out-point cuts and the file contracts with Next.
/// </summary>
public static class SpikeBuilder
{
    public static SpikeResult Build(SpikeRequest req)
    {
        var warnings = new List<string>();
        long contentMs = req.Episode.DurationMs;
        IReadOnlyList<Act> acts = BreakPlanner.PlanActs(contentMs, req.Episode.Chapters, req.BreakPolicy);

        DateTimeOffset slotEnd = req.SlotEnd ?? Padding.SlotEnd(req.Start, contentMs, req.PaddingPolicy);
        long padMs = (long)(slotEnd - req.Start).TotalMilliseconds - contentMs;
        if (padMs < 0)
        {
            warnings.Add($"Slot end {slotEnd:HH:mm} is before the content ends; padding set to zero.");
            slotEnd = req.Start.AddMilliseconds(contentMs);
            padMs = 0;
        }
        if (req.PaddingPolicy.MaxPadMs is { } maxPad && padMs > maxPad)
            warnings.Add($"Pad of {padMs / 1000}s exceeds the max of {maxPad / 1000}s.");

        // Break positions: optional pre-roll, one after each act except the last, optional post-roll.
        int midBreaks = acts.Count - 1;
        int breakCount = midBreaks + (req.BreakPolicy.PreRoll ? 1 : 0) + (req.BreakPolicy.PostRoll ? 1 : 0);
        if (breakCount == 0 && padMs > 0)
        {
            warnings.Add("No break positions, so all padding lands after the show.");
            breakCount = 1;
        }

        long[] budgets = SplitBudget(padMs, breakCount, req.BreakPolicy.PreRoll);

        var items = new List<PlayoutItem>();
        var cursor = req.Start;
        int breakIndex = 0;
        var filler = new BreakFiller(req.Ads) { MaxRepeatsPerBreak = Math.Max(1, req.MaxRepeatsPerBreak) };
        var bumpers = new BumperRotation(req.Bumpers);
        var gaps = new GapFiller(req.Filler);

        if (req.BreakPolicy.PreRoll)
            cursor = EmitBreak(items, cursor, budgets[breakIndex++], filler, bumpers, gaps, warnings);

        for (int i = 0; i < acts.Count; i++)
        {
            Act act = acts[i];
            items.Add(new PlayoutItem
            {
                Id = $"act-{i + 1}",
                Start = cursor,
                Finish = cursor.AddMilliseconds(act.DurationMs),
                Source = new LocalSource
                {
                    Path = req.Episode.Path,
                    InPointMs = act.InMs,
                    OutPointMs = act.OutMs,
                },
            });
            cursor = cursor.AddMilliseconds(act.DurationMs);

            bool isLast = i == acts.Count - 1;
            if (!isLast || req.BreakPolicy.PostRoll || (breakCount == 1 && midBreaks == 0 && !req.BreakPolicy.PreRoll))
            {
                if (breakIndex < budgets.Length)
                    cursor = EmitBreak(items, cursor, budgets[breakIndex++], filler, bumpers, gaps, warnings);
            }
        }

        // Absorb rounding so the slot ends on the exact boundary.
        long drift = (long)(slotEnd - cursor).TotalMilliseconds;
        if (drift > 0)
        {
            gaps.Emit(items, cursor, drift);
            cursor = slotEnd;
        }
        else if (drift < 0)
        {
            warnings.Add($"Items overran the slot end by {-drift}ms.");
        }

        var playout = new Playout.Playout { Items = items };
        var programme = new GuideProgramme
        {
            Start = req.Start,
            Stop = slotEnd,
            Title = req.Title,
        };

        return new SpikeResult
        {
            Playout = playout,
            Programme = programme,
            Acts = acts,
            PadMs = padMs,
            Warnings = warnings.GroupBy(w => w).Select(g => g.Count() > 1 ? $"{g.Key} (x{g.Count()})" : g.Key).ToList(),
        };
    }

    /// <summary>Mid-show breaks share the pad evenly; a pre-roll gets half a share.</summary>
    private static long[] SplitBudget(long padMs, int breaks, bool preRoll)
    {
        if (breaks <= 0) return [];
        var weights = new double[breaks];
        for (int i = 0; i < breaks; i++) weights[i] = 1.0;
        if (preRoll) weights[0] = 0.5;
        double total = weights.Sum();

        var budgets = new long[breaks];
        long assigned = 0;
        for (int i = 0; i < breaks; i++)
        {
            budgets[i] = (long)Math.Floor(padMs * weights[i] / total);
            assigned += budgets[i];
        }
        budgets[^1] += padMs - assigned; // remainder to the last break
        return budgets;
    }

    private static DateTimeOffset EmitBreak(
        List<PlayoutItem> items, DateTimeOffset cursor, long budgetMs, BreakFiller filler, BumperRotation bumpers, GapFiller gaps, List<string> warnings)
    {
        if (budgetMs <= 0) return cursor;
        int n = items.Count(i => i.Id.StartsWith("ad-")) + 1;
        int b = items.Count(i => i.Id.StartsWith("bumper-")) + 1;

        // Bumpers first: reserve them only when enough remains for ads.
        FillerClip? before = bumpers.NextBefore(budgetMs);
        long afterBefore = budgetMs - (before?.DurationMs ?? 0);
        FillerClip? after = bumpers.NextAfter(afterBefore);
        long adBudget = afterBefore - (after?.DurationMs ?? 0);

        if (before is not null)
        {
            items.Add(Clip($"bumper-{b++}", cursor, before));
            cursor = cursor.AddMilliseconds(before.DurationMs);
        }

        FillResult fill = filler.Fill(adBudget);
        foreach (FillerClip clip in fill.Clips)
        {
            items.Add(Clip($"ad-{n++}", cursor, clip));
            cursor = cursor.AddMilliseconds(clip.DurationMs);
        }

        if (fill.RemainderMs > 0)
        {
            if (fill.RemainderMs > 5_000 && filler.HasClips && !gaps.HasFiller)
                warnings.Add($"{fill.RemainderMs / 1000}s of a break filled with slate: no clip combination fits.");
            gaps.Emit(items, cursor, fill.RemainderMs);
            cursor = cursor.AddMilliseconds(fill.RemainderMs);
        }

        if (after is not null)
        {
            items.Add(Clip($"bumper-{b++}", cursor, after));
            cursor = cursor.AddMilliseconds(after.DurationMs);
        }
        return cursor;
    }

    private static PlayoutItem Clip(string id, DateTimeOffset start, FillerClip clip) => new()
    {
        Id = id,
        Start = start,
        Finish = start.AddMilliseconds(clip.DurationMs),
        Source = new LocalSource { Path = clip.Path },
    };

    /// <summary>
    /// Covers leftover time. With a filler clip, the gap plays from the clip's start and loops as needed,
    /// each segment at least <see cref="MinSegmentMs"/> long. Without one, a black slate.
    /// </summary>
    private sealed class GapFiller(FillerClip? filler)
    {
        public const long MinSegmentMs = 2_000;
        private int _n;

        public bool HasFiller => filler is not null && filler.DurationMs >= MinSegmentMs;

        public void Emit(List<PlayoutItem> items, DateTimeOffset start, long gapMs)
        {
            if (!HasFiller)
            {
                items.Add(Slate($"slate-{items.Count}", start, gapMs));
                return;
            }

            long remaining = gapMs;
            DateTimeOffset cursor = start;
            while (remaining > 0)
            {
                long segment = Math.Min(remaining, filler!.DurationMs);
                // Never leave a sliver for the next loop: fold it into this segment's budget by shortening this one.
                if (remaining - segment is > 0 and < MinSegmentMs) segment = remaining - MinSegmentMs;
                items.Add(new PlayoutItem
                {
                    Id = $"filler-{++_n}",
                    Start = cursor,
                    Finish = cursor.AddMilliseconds(segment),
                    Source = new LocalSource { Path = filler.Path, InPointMs = 0, OutPointMs = segment },
                });
                cursor = cursor.AddMilliseconds(segment);
                remaining -= segment;
            }
        }
    }

    /// <summary>Round-robin over each bumper list so the same bumper is not seen every break.</summary>
    private sealed class BumperRotation(BumperSet set)
    {
        private int _before, _after;

        public FillerClip? NextBefore(long budgetMs) => Pick(set.Before, ref _before, budgetMs);
        public FillerClip? NextAfter(long budgetMs) => Pick(set.After, ref _after, budgetMs);

        private FillerClip? Pick(IReadOnlyList<FillerClip> list, ref int index, long budgetMs)
        {
            if (list.Count == 0) return null;
            for (int tries = 0; tries < list.Count; tries++)
            {
                FillerClip candidate = list[index % list.Count];
                index++;
                if (budgetMs - candidate.DurationMs >= set.MinAdMs) return candidate;
            }
            return null;
        }
    }

    /// <summary>
    /// Fills a stretch of time with no show in it (a station break that holds the grid): bumpers, clips, filler.
    /// Returns the items placed from <paramref name="start"/> for exactly <paramref name="durationMs"/>.
    /// </summary>
    public static (IReadOnlyList<PlayoutItem> Items, IReadOnlyList<string> Warnings) FillGap(
        DateTimeOffset start, long durationMs, IReadOnlyList<FillerClip> clips, BumperSet bumpers, FillerClip? filler, int maxRepeatsPerBreak = 1)
    {
        var items = new List<PlayoutItem>();
        var warnings = new List<string>();
        if (durationMs <= 0) return (items, warnings);
        var breakFiller = new BreakFiller(clips) { MaxRepeatsPerBreak = Math.Max(1, maxRepeatsPerBreak) };
        var rotation = new BumperRotation(bumpers);
        var gaps = new GapFiller(filler);
        DateTimeOffset end = EmitBreak(items, start, durationMs, breakFiller, rotation, gaps, warnings);
        long drift = (long)(start.AddMilliseconds(durationMs) - end).TotalMilliseconds;
        if (drift > 0) gaps.Emit(items, end, drift);
        return (items, warnings);
    }

    /// <summary>Black video with silent audio, generated by ffmpeg's lavfi, for the last seconds of a break.</summary>
    public static PlayoutItem Slate(string id, DateTimeOffset start, long durationMs)
    {
        double seconds = durationMs / 1000.0;
        string d = seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return new PlayoutItem
        {
            Id = id,
            Start = start,
            Finish = start.AddMilliseconds(durationMs),
            Tracks = new PlayoutItemTracks
            {
                Video = new TrackSelection
                {
                    Source = new LavfiSource { Params = $"color=c=black:s=1920x1080:r=30:d={d}" },
                },
                Audio = new TrackSelection
                {
                    Source = new LavfiSource { Params = $"anullsrc=channel_layout=stereo:sample_rate=48000:d={d}" },
                },
            },
        };
    }
}
