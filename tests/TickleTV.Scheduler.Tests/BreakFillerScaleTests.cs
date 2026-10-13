using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Tests;

/// <summary>
/// A whole library of commercials must not blow up a break: the old full knapsack table was clips times
/// budget cells, which with twenty thousand clips and a twenty-minute station break meant gigabytes per break.
/// </summary>
public class BreakFillerScaleTests
{
    private static List<FillerClip> BigLibrary(int count) =>
        Enumerable.Range(0, count).Select(i => new FillerClip($"/ads/{i}.mp4", 10_000 + (i % 25) * 2_000)).ToList();

    [Fact]
    public void Twenty_thousand_clips_and_a_long_break_stay_cheap_and_fill_exactly()
    {
        var filler = new BreakFiller(BigLibrary(20_000)) { MaxRepeatsPerBreak = 2, Random = new Random(3) };

        long before = GC.GetAllocatedBytesForCurrentThread();
        FillResult r = filler.Fill(20 * 60_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, r.RemainderMs);
        Assert.Equal(20 * 60_000, r.Clips.Sum(c => c.DurationMs));
        Assert.True(allocated < 64 * 1024 * 1024, $"one break allocated {allocated / 1048576.0:N1} MB");
    }

    [Fact]
    public void Different_breaks_see_different_clips_from_a_big_library()
    {
        var a = new BreakFiller(BigLibrary(5_000)) { Random = new Random(1) };
        var b = new BreakFiller(BigLibrary(5_000)) { Random = new Random(2) };

        var pathsA = a.Fill(180_000).Clips.Select(c => c.Path).ToHashSet();
        var pathsB = b.Fill(180_000).Clips.Select(c => c.Path).ToHashSet();

        Assert.NotEmpty(pathsA);
        Assert.False(pathsA.SetEquals(pathsB), "two breaks drew exactly the same clips from a library of thousands");
    }

    [Fact]
    public void Later_breaks_from_a_big_library_prefer_clips_not_yet_played()
    {
        var filler = new BreakFiller(BigLibrary(2_000)) { MaxRepeatsPerBreak = 1, MaxCandidates = 50, Random = new Random(9) };
        var first = filler.Fill(120_000).Clips.Select(c => c.Path).ToHashSet();
        var second = filler.Fill(120_000).Clips.Select(c => c.Path).ToHashSet();
        Assert.NotEmpty(first);
        Assert.Empty(first.Intersect(second));
    }

    [Fact]
    public void A_small_library_still_gets_the_exact_optimum()
    {
        // 30 + 15 + 10 + 10 fills 65 s exactly; the capped path must not kick in and change that.
        var filler = new BreakFiller([new("/a.mp4", 30_000), new("/b.mp4", 15_000), new("/c.mp4", 10_000)]) { MaxRepeatsPerBreak = 2 };
        FillResult r = filler.Fill(65_000);
        Assert.Equal(0, r.RemainderMs);
    }
}
