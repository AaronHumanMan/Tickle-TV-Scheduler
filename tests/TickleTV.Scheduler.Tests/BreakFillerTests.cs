using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Tests;

public class BreakFillerTests
{
    private static readonly FillerClip A30 = new("/ads/a30.mp4", 30_000);
    private static readonly FillerClip B15 = new("/ads/b15.mp4", 15_000);
    private static readonly FillerClip C10 = new("/ads/c10.mp4", 10_000);

    [Fact]
    public void Finds_the_combination_that_fills_the_budget_exactly()
    {
        var filler = new BreakFiller([A30, B15, C10]);
        FillResult r = filler.Fill(70_000);

        // Greedy longest-first with no repeats would stop at 55s; 30+15+15+10 is exact.
        Assert.Equal(0, r.RemainderMs);
        Assert.Equal(70_000, r.Clips.Sum(c => c.DurationMs));
    }

    [Fact]
    public void Never_plays_the_same_clip_back_to_back_when_avoidable()
    {
        var filler = new BreakFiller([A30, B15, C10]);
        FillResult r = filler.Fill(70_000);

        for (int i = 1; i < r.Clips.Count; i++)
            Assert.NotEqual(r.Clips[i - 1].Path, r.Clips[i].Path);
    }

    [Fact]
    public void Leftover_is_reported_when_nothing_fits()
    {
        var filler = new BreakFiller([A30]);
        FillResult r = filler.Fill(20_000);

        Assert.Empty(r.Clips);
        Assert.Equal(20_000, r.RemainderMs);
    }

    [Fact]
    public void Repeats_are_bounded_per_break()
    {
        var filler = new BreakFiller([C10]) { MaxRepeatsPerBreak = 2 };
        FillResult r = filler.Fill(60_000);

        Assert.Equal(2, r.Clips.Count);
        Assert.Equal(40_000, r.RemainderMs);
    }

    [Fact]
    public void Later_breaks_prefer_clips_played_less()
    {
        var filler = new BreakFiller([new FillerClip("/x.mp4", 30_000), new FillerClip("/y.mp4", 30_000)]) { MaxRepeatsPerBreak = 1 };

        FillResult first = filler.Fill(30_000);
        FillResult second = filler.Fill(30_000);

        Assert.NotEqual(first.Clips[0].Path, second.Clips[0].Path);
    }

    [Fact]
    public void Never_exceeds_the_real_budget_after_rounding()
    {
        var filler = new BreakFiller([new FillerClip("/odd.mp4", 10_040)]) { MaxRepeatsPerBreak = 3 };
        FillResult r = filler.Fill(30_100); // rounds to 301 units; three clips are 301 units but 30,120 ms

        Assert.True(r.Clips.Sum(c => c.DurationMs) <= 30_100);
        Assert.Equal(2, r.Clips.Count);
    }

    [Fact]
    public void Never_leaves_a_slate_shorter_than_two_seconds()
    {
        // 30 + 15 + 10 = 55 s against a 55.3 s budget would leave a 300 ms slate, which ffmpeg rejects.
        var filler = new BreakFiller([A30, B15, C10]) { MaxRepeatsPerBreak = 1 };
        FillResult r = filler.Fill(55_300);

        Assert.True(r.RemainderMs == 0 || r.RemainderMs >= 2_000, $"left {r.RemainderMs} ms");
        Assert.True(r.RemainderMs <= 15_300, $"gave up too much: left {r.RemainderMs} ms");
    }
}
