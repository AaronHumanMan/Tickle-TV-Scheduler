using TickleTV.Scheduler.Core.Engine;
using TickleTV.Scheduler.Core.Media;
using TickleTV.Scheduler.Core.Playout;

namespace TickleTV.Scheduler.Tests;

public class GapFillerTests
{
    private const long Min = 60_000;
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 20, 0, 0, TimeSpan.FromHours(-5));

    private static SpikeRequest Request(FillerClip? filler, params FillerClip[] ads) => new()
    {
        Episode = new MediaProbe { Path = "/tv/e.mkv", DurationMs = 22 * Min, Chapters = [], Streams = [] },
        Title = "Show",
        Start = Start,
        Ads = ads,
        Filler = filler,
        BreakPolicy = new BreakPolicy { PostRoll = true, FallbackBreaks = 0 },
        PaddingPolicy = new PaddingPolicy { Mode = PaddingMode.HalfHour },
    };

    [Fact]
    public void Without_a_filler_the_gap_is_a_black_slate()
    {
        SpikeResult r = SpikeBuilder.Build(Request(null, new FillerClip("/ads/a.mp4", 7 * Min)));
        Assert.Contains(r.Playout.Items, i => i.Tracks?.Video?.Source is LavfiSource);
        Assert.DoesNotContain(r.Playout.Items, i => i.Id.StartsWith("filler-"));
    }

    [Fact]
    public void With_a_filler_the_gap_plays_the_clip_with_in_and_out_points_and_loops()
    {
        // 8-minute pad, one 7-minute ad: a 60 s gap covered by a 25 s filler = 25 + 25 + 10 s segments.
        var filler = new FillerClip("/filler/vaporwave.mp4", 25_000);
        SpikeResult r = SpikeBuilder.Build(Request(filler, new FillerClip("/ads/a.mp4", 7 * Min)));

        var segments = r.Playout.Items.Where(i => i.Id.StartsWith("filler-")).ToList();
        Assert.DoesNotContain(r.Playout.Items, i => i.Tracks?.Video?.Source is LavfiSource);
        Assert.Equal(3, segments.Count);
        Assert.All(segments, s =>
        {
            var src = Assert.IsType<LocalSource>(s.Source);
            Assert.Equal("/filler/vaporwave.mp4", src.Path);
            Assert.Equal(0, src.InPointMs);
            Assert.Equal((long)(s.Finish - s.Start).TotalMilliseconds, src.OutPointMs);
            Assert.True(src.OutPointMs >= 2_000);
        });
        Assert.Equal(60_000, segments.Sum(s => (long)(s.Finish - s.Start).TotalMilliseconds));
        Assert.Equal(Start.AddMinutes(30), r.Playout.Items[^1].Finish);
    }

    [Fact]
    public void A_sliver_left_after_looping_is_folded_into_the_previous_segment()
    {
        // 60 s gap, 29 s filler: 29 + 29 + 2 would leave a 2 s tail, which is allowed; 29.5 s would leave 1 s, which is not.
        var filler = new FillerClip("/filler/f.mp4", 29_500);
        SpikeResult r = SpikeBuilder.Build(Request(filler, new FillerClip("/ads/a.mp4", 7 * Min)));

        var lengths = r.Playout.Items.Where(i => i.Id.StartsWith("filler-")).Select(i => (long)(i.Finish - i.Start).TotalMilliseconds).ToList();
        Assert.Equal(60_000, lengths.Sum());
        Assert.All(lengths, l => Assert.True(l >= 2_000, $"segment of {l} ms"));
    }
}
