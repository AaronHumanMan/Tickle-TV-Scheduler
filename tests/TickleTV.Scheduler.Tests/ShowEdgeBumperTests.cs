using TickleTV.Scheduler.Core.Engine;
using TickleTV.Scheduler.Core.Media;
using TickleTV.Scheduler.Core.Playout;

namespace TickleTV.Scheduler.Tests;

public class ShowEdgeBumperTests
{
    private const long Min = 60_000;
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 20, 0, 0, TimeSpan.FromHours(-5));

    private static SpikeRequest Request(BumperPlacement placement) => new()
    {
        Episode = new MediaProbe
        {
            Path = "/tv/e.mkv", DurationMs = 22 * Min,
            Chapters = [new Chapter(0, 7 * Min, null), new Chapter(7 * Min, 15 * Min, null), new Chapter(15 * Min, 22 * Min, null)],
            Streams = [],
        },
        Title = "Show",
        Start = Start,
        Ads = Enumerable.Range(0, 12).Select(i => new FillerClip($"/ads/{i}.mp4", 30_000 + i * 2_500)).ToList(),
        Bumpers = new BumperSet
        {
            Before = [new FillerClip("/b/id1.mp4", 5_000), new FillerClip("/b/id2.mp4", 6_000)],
            After = [new FillerClip("/b/card.mp4", 4_000)],
            Placement = placement,
        },
        BreakPolicy = new BreakPolicy { MaxMidBreaks = 2, MinActMs = 2 * Min, PostRoll = true },
        PaddingPolicy = new PaddingPolicy { Mode = PaddingMode.HalfHour },
    };

    [Fact]
    public void Show_edges_means_one_bumper_before_the_show_and_one_after_it_and_none_in_the_breaks()
    {
        SpikeResult r = SpikeBuilder.Build(Request(BumperPlacement.ShowEdges));
        var items = r.Playout.Items.ToList();

        var bumpers = items.Where(i => i.Id.StartsWith("bumper-")).ToList();
        Assert.Equal(2, bumpers.Count);

        // First item is the intro bumper, then act 1.
        Assert.StartsWith("bumper-", items[0].Id);
        Assert.Equal("act-1", items[1].Id);

        // The outro bumper comes right after the last act, before the post-roll ads.
        int lastAct = items.FindLastIndex(i => i.Id.StartsWith("act-"));
        Assert.StartsWith("bumper-", items[lastAct + 1].Id);
        Assert.StartsWith("ad-", items[lastAct + 2].Id);

        // Mid-show breaks hold only ads (and maybe filler).
        for (int i = 1; i < items.Count; i++)
            if (items[i - 1].Id.StartsWith("act-") && !items[i].Id.StartsWith("act-") && i != lastAct + 1)
                Assert.StartsWith("ad-", items[i].Id);

        Assert.Equal(Start.AddMinutes(30), items[^1].Finish);
        for (int i = 1; i < items.Count; i++) Assert.Equal(items[i - 1].Finish, items[i].Start);
    }

    [Fact]
    public void Show_edges_leaves_more_time_for_commercials_than_every_break()
    {
        long AdTime(BumperPlacement p) => SpikeBuilder.Build(Request(p)).Playout.Items.Where(i => i.Id.StartsWith("ad-")).Sum(i => (long)(i.Finish - i.Start).TotalMilliseconds);
        Assert.True(AdTime(BumperPlacement.ShowEdges) > AdTime(BumperPlacement.EveryBreak));
    }
}
