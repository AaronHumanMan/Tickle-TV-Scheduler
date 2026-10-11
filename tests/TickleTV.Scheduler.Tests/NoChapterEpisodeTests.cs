using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Tests;

/// <summary>A show with no chapter marks runs straight through and is padded only at the end, to the next 5 minutes.</summary>
public class NoChapterEpisodeTests : IDisposable
{
    private const long Min = 60_000;
    private readonly CatalogDb _db = CatalogDb.InMemory();
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

    public void Dispose() => _db.Dispose();

    private void Seed(bool withChapters)
    {
        long id = _db.Insert(new MediaItem
        {
            ContentHash = "e1", Path = "/tv/doc/e1.mkv", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch,
            Kind = MediaKind.Episode, KindSource = FactSource.Filename, DurationMs = 43 * Min + 20_000, ProbedUtc = DateTimeOffset.UnixEpoch, Title = "e1",
        });
        _db.UpsertEpisode(id, new EpisodeInfo { Show = "Doc", Season = 1, Episode = 1, Source = FactSource.Filename });
        if (withChapters)
            _db.ReplaceChapters(id, [new CatalogChapter(0, 0, 10 * Min, null), new CatalogChapter(1, 10 * Min, 25 * Min, null), new CatalogChapter(2, 25 * Min, 43 * Min, null)]);
        for (int i = 0; i < 10; i++)
            _db.Insert(new MediaItem { ContentHash = $"a{i}", Path = $"/ads/{i}.mp4", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch, Kind = MediaKind.Commercial, KindSource = FactSource.Filename, DurationMs = 30_000 + i * 5_000, ProbedUtc = DateTimeOffset.UnixEpoch });
    }

    private static ChannelConfig Channel() => new()
    {
        Number = "1",
        Defaults = new SlotDefaults { Pad = "60", PostRoll = true, MaxBreaks = 3 },
        Schedule = [new SlotConfig { Time = "20:00", Show = "Doc", Order = "chronological" }],
    };

    [Fact]
    public void Without_chapters_no_mid_show_breaks_and_padding_to_the_next_five_minutes()
    {
        Seed(withChapters: false);
        var from = new DateTimeOffset(2026, 10, 11, 20, 0, 0, Offset);
        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5").Build(from, from.AddMinutes(30));

        var first = r.Programmes[0];
        Assert.Equal(from.AddMinutes(45), first.Stop); // 43:20 pads to 45:00, not to the hour

        var items = r.Playout.Items.TakeWhile(i => i.Start < first.Stop).ToList();
        var acts = items.Where(i => i.Id.StartsWith("act-")).ToList();
        Assert.Single(acts);
        Assert.Equal(from, acts[0].Start);
        // Every ad sits after the single act, never inside the show.
        Assert.All(items.Where(i => i.Id.StartsWith("ad-")), ad => Assert.True(ad.Start >= acts[0].Finish));
    }

    [Fact]
    public void With_chapters_the_slot_padding_still_applies()
    {
        Seed(withChapters: true);
        var from = new DateTimeOffset(2026, 10, 11, 20, 0, 0, Offset);
        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5").Build(from, from.AddMinutes(30));

        Assert.Equal(from.AddMinutes(60), r.Programmes[0].Stop);
        Assert.True(r.Playout.Items.Count(i => i.Id.StartsWith("act-")) > 1);
    }
}
