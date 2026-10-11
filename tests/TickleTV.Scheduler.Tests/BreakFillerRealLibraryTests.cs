using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Tests;

/// <summary>Regression: real commercial durations from a 19-clip library left 30s+ of slate per break.</summary>
public class BreakFillerRealLibraryTests
{
    private static readonly long[] RealDurationsMs =
    [
        32461, 32485, 32067, 34296, 36525, 16440, 30766, 36757, 32136, 34667,
        32879, 36479, 122300, 36432, 64459, 34040, 14629, 31533, 90279,
    ];

    private static List<FillerClip> Library() =>
        RealDurationsMs.Select((d, i) => new FillerClip($"/ads/clip{i}.mp4", d)).ToList();

    [Fact]
    public void A_217s_break_is_filled_to_within_two_seconds()
    {
        var filler = new BreakFiller(Library());
        FillResult r = filler.Fill(217_060);

        Assert.True(r.RemainderMs < 2_000, $"left {r.RemainderMs} ms");
        Assert.True(r.Clips.Sum(c => c.DurationMs) <= 217_060);
    }

    [Fact]
    public void Four_breaks_in_a_row_all_fill_well_and_never_overshoot()
    {
        var filler = new BreakFiller(Library());
        for (int i = 0; i < 4; i++)
        {
            FillResult r = filler.Fill(217_060);
            Assert.True(r.RemainderMs < 2_000, $"break {i + 1} left {r.RemainderMs} ms");
            Assert.True(r.Clips.Sum(c => c.DurationMs) <= 217_060);
        }
    }

    [Fact]
    public void With_19_clips_a_break_does_not_repeat_a_clip()
    {
        var filler = new BreakFiller(Library());
        FillResult r = filler.Fill(217_060);

        Assert.Equal(r.Clips.Count, r.Clips.Select(c => c.Path).Distinct().Count());
    }

    [Fact]
    public void Rotation_spreads_across_the_library_over_several_breaks()
    {
        var filler = new BreakFiller(Library());
        var seen = new HashSet<string>();
        for (int i = 0; i < 3; i++)
            foreach (FillerClip c in filler.Fill(217_060).Clips) seen.Add(c.Path);

        // Three breaks of ~217s hold roughly 18 clip plays; most of the 19 clips should have aired.
        Assert.True(seen.Count >= 15, $"only {seen.Count} distinct clips aired");
    }
}
