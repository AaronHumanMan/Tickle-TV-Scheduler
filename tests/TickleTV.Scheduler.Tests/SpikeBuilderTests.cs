using TickleTV.Scheduler.Core.Engine;
using TickleTV.Scheduler.Core.Media;
using TickleTV.Scheduler.Core.Playout;

namespace TickleTV.Scheduler.Tests;

public class SpikeBuilderTests
{
    private const long Min = 60_000;
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 20, 0, 0, TimeSpan.FromHours(-5));

    private static MediaProbe Episode(long durationMs, params long[] chapterStarts)
    {
        var chapters = new List<Chapter>();
        for (int i = 0; i < chapterStarts.Length; i++)
        {
            long end = i + 1 < chapterStarts.Length ? chapterStarts[i + 1] : durationMs;
            chapters.Add(new Chapter(chapterStarts[i], end, null));
        }
        return new MediaProbe { Path = "/tv/show.mkv", DurationMs = durationMs, Chapters = chapters, Streams = [] };
    }

    [Fact]
    public void Slot_ends_exactly_on_the_boundary_and_items_are_contiguous()
    {
        var req = new SpikeRequest
        {
            Episode = Episode(22 * Min + 10_000, 0, 5 * Min, 11 * Min, 17 * Min),
            Title = "Show",
            Start = Start,
            Ads =
            [
                new FillerClip("/ads/a30.mp4", 30_000),
                new FillerClip("/ads/b15.mp4", 15_000),
                new FillerClip("/ads/c60.mp4", 60_000),
                new FillerClip("/ads/d10.mp4", 10_000),
                new FillerClip("/ads/e5.mp4", 5_000),
            ],
            BreakPolicy = new BreakPolicy { MaxMidBreaks = 3, MinActMs = 2 * Min, PostRoll = true },
            PaddingPolicy = new PaddingPolicy { Mode = PaddingMode.HalfHour },
        };

        SpikeResult r = SpikeBuilder.Build(req);

        Assert.Equal(Start.AddMinutes(30), r.Programme.Stop);
        Assert.Equal(Start, r.Playout.Items[0].Start);
        Assert.Equal(Start.AddMinutes(30), r.Playout.Items[^1].Finish);
        for (int i = 1; i < r.Playout.Items.Count; i++)
            Assert.Equal(r.Playout.Items[i - 1].Finish, r.Playout.Items[i].Start);

        // All four acts present, in order, covering the whole file.
        var acts = r.Playout.Items.Where(i => i.Id.StartsWith("act-")).Select(i => (LocalSource)i.Source!).ToList();
        Assert.Equal(4, acts.Count);
        Assert.Equal(0, acts[0].InPointMs);
        Assert.Equal(22 * Min + 10_000, acts[^1].OutPointMs);

        // Ads fill the pad; no two identical ads play back to back on air.
        Assert.Contains(r.Playout.Items, i => i.Id.StartsWith("ad-"));
        for (int i = 1; i < r.Playout.Items.Count; i++)
        {
            if (r.Playout.Items[i - 1].Source is LocalSource prev && r.Playout.Items[i].Source is LocalSource cur
                && r.Playout.Items[i - 1].Id.StartsWith("ad-") && r.Playout.Items[i].Id.StartsWith("ad-"))
                Assert.NotEqual(prev.Path, cur.Path);
        }

        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Without_ads_breaks_are_slate_and_still_land_on_the_boundary()
    {
        var req = new SpikeRequest
        {
            Episode = Episode(22 * Min, 0, 11 * Min),
            Title = "Show",
            Start = Start,
            PaddingPolicy = new PaddingPolicy { Mode = PaddingMode.HalfHour },
        };

        SpikeResult r = SpikeBuilder.Build(req);

        Assert.Equal(Start.AddMinutes(30), r.Playout.Items[^1].Finish);
        Assert.Contains(r.Playout.Items, i => i.Tracks?.Video?.Source is LavfiSource);
        long total = r.Playout.Items.Sum(i => (long)(i.Finish - i.Start).TotalMilliseconds);
        Assert.Equal(30 * Min, total);
    }

    [Fact]
    public void No_padding_mode_emits_only_the_acts()
    {
        var req = new SpikeRequest
        {
            Episode = Episode(22 * Min, 0, 11 * Min),
            Title = "Show",
            Start = Start,
            BreakPolicy = new BreakPolicy { PostRoll = false },
            PaddingPolicy = new PaddingPolicy { Mode = PaddingMode.None },
        };

        SpikeResult r = SpikeBuilder.Build(req);

        Assert.All(r.Playout.Items, i => Assert.StartsWith("act-", i.Id));
        Assert.Equal(Start.AddMinutes(22), r.Playout.Items[^1].Finish);
    }
}
