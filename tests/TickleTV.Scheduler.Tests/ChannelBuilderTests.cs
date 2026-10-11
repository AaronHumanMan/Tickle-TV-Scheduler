using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;
using TickleTV.Scheduler.Core.Playout;

namespace TickleTV.Scheduler.Tests;

public class ChannelBuilderTests : IDisposable
{
    private const long Min = 60_000;
    private readonly CatalogDb _db = CatalogDb.InMemory();
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

    public void Dispose() => _db.Dispose();

    private long AddItem(string path, MediaKind kind, long durationMs, int? year, string? show = null, int? ep = null, DateOnly? airDate = null, params (long start, long end)[] chapters)
    {
        long id = _db.Insert(new MediaItem
        {
            ContentHash = Guid.NewGuid().ToString("N"), Path = path, FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch,
            Kind = kind, KindSource = FactSource.Filename, Year = year, DurationMs = durationMs, ProbedUtc = DateTimeOffset.UnixEpoch,
            Title = Path.GetFileNameWithoutExtension(path),
        });
        if (show is not null)
            _db.UpsertEpisode(id, new EpisodeInfo { Show = show, Season = 1, Episode = ep, AirDate = airDate, EpisodeTitle = $"Ep {ep}", Source = FactSource.Filename });
        _db.ReplaceChapters(id, chapters.Select((c, i) => new CatalogChapter(i, c.start, c.end, null)));
        return id;
    }

    private void SeedLibrary()
    {
        for (int i = 1; i <= 4; i++)
            AddItem($"/tv/tng/e{i}.mkv", MediaKind.Episode, 45 * Min, 1990, "TNG", i, new DateOnly(1990, 10, i),
                (0, 10 * Min), (10 * Min, 22 * Min), (22 * Min, 34 * Min), (34 * Min, 45 * Min));
        for (int i = 1; i <= 6; i++)
            AddItem($"/tv/simpsons/e{i}.mkv", MediaKind.Episode, 22 * Min, 1994, "Simpsons", i, null,
                (0, 7 * Min), (7 * Min, 15 * Min), (15 * Min, 22 * Min));
        for (int i = 0; i < 20; i++)
            AddItem($"/ads/ad{i}.mp4", MediaKind.Commercial, 30_000 + (i % 4) * 15_000, 1988 + i % 8);
    }

    private static ChannelConfig Channel(string pad = "30") => new()
    {
        Number = "1",
        TvgId = "t.1",
        Defaults = new SlotDefaults { Pad = pad, MaxBreaks = 3, MinActSeconds = 120, PostRoll = true },
        Ads = new AdsConfig { Pool = "kind = commercial", CooldownMinutes = 60, MaxRepeatsPerBreak = 1 },
        Schedule =
        [
            new SlotConfig { Time = "20:00", Show = "TNG", Order = "chronological", Pad = "60" },
            new SlotConfig { Time = "22:00", Show = "Simpsons", Order = "shuffle" },
        ],
    };

    [Fact]
    public void Grid_airs_the_right_show_at_the_right_time_and_items_are_contiguous()
    {
        SeedLibrary();
        var builder = new ChannelBuilder(_db, new EpisodePicker(_db, seed: 3), Channel(), "0.0.5");
        var from = new DateTimeOffset(2026, 10, 11, 20, 0, 0, Offset);

        BuildResult r = builder.Build(from, from.AddHours(4));

        // 20:00 and 21:00 are TNG in hour slots; from 22:00 Simpsons in half-hour slots.
        Assert.Equal("TNG", r.Programmes[0].Title);
        Assert.Equal(from.AddHours(1), r.Programmes[0].Stop);
        Assert.Equal("TNG", r.Programmes[1].Title);
        Assert.Equal("Simpsons", r.Programmes[2].Title);
        Assert.Equal(from.AddHours(2), r.Programmes[2].Start);
        Assert.Equal(from.AddHours(2.5), r.Programmes[2].Stop);

        for (int i = 1; i < r.Playout.Items.Count; i++)
            Assert.Equal(r.Playout.Items[i - 1].Finish, r.Playout.Items[i].Start);
        Assert.True(r.End >= from.AddHours(4));
        Assert.Equal(r.Playout.Items.Count, r.Playout.Items.Select(i => i.Id).Distinct().Count());
        Assert.Equal("https://ersatztv.org/playout/version/0.0.5", r.Playout.Version);
    }

    [Fact]
    public void Chronological_slot_advances_across_builds_and_plays_are_recorded()
    {
        SeedLibrary();
        var from = new DateTimeOffset(2026, 10, 11, 20, 0, 0, Offset);

        BuildResult first = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5").Build(from, from.AddHours(1));
        foreach (PlayRecord p in first.Plays) _db.RecordPlay(p.ItemId, "1", p.AiredAt, p.Context);
        BuildResult second = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5").Build(from.AddDays(1), from.AddDays(1).AddHours(1));

        Assert.Equal("S01E01", first.Programmes[0].EpisodeNumber);
        Assert.Equal("S01E02", second.Programmes[0].EpisodeNumber);
        Assert.Contains(first.Plays, p => p.Context.StartsWith("slot 20:00"));
        Assert.Contains(first.Plays, p => p.Context.StartsWith("ad in"));
    }

    [Fact]
    public void Ad_cooldown_keeps_a_clip_from_returning_too_soon()
    {
        SeedLibrary();
        var from = new DateTimeOffset(2026, 10, 11, 20, 0, 0, Offset);
        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5").Build(from, from.AddHours(1));

        var adPlays = r.Plays.Where(p => p.Context.StartsWith("ad in")).GroupBy(p => p.ItemId);
        foreach (var g in adPlays)
        {
            var times = g.Select(p => p.AiredAt).OrderBy(t => t).ToList();
            for (int i = 1; i < times.Count; i++)
                Assert.True(times[i] - times[i - 1] >= TimeSpan.FromMinutes(60), "a clip aired twice within the cooldown");
        }
    }

    [Fact]
    public void Missing_show_becomes_slate_with_a_warning_not_a_crash()
    {
        SeedLibrary();
        ChannelConfig ch = Channel();
        ch.Schedule = [new SlotConfig { Time = "20:00", Show = "Nothing", Order = "shuffle" }, new SlotConfig { Time = "21:00", Show = "TNG" }];
        var from = new DateTimeOffset(2026, 10, 11, 20, 0, 0, Offset);

        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), ch, "0.0.5").Build(from, from.AddHours(2));

        Assert.Contains(r.Warnings, w => w.Contains("Nothing"));
        Assert.IsType<LavfiSource>(r.Playout.Items[0].Tracks?.Video?.Source);
        Assert.Equal(from.AddHours(1), r.Playout.Items[0].Finish);
        Assert.Equal("TNG", r.Programmes[1].Title);
    }

    [Fact]
    public void Window_starting_before_the_first_slot_uses_the_last_slot_of_the_previous_day()
    {
        SeedLibrary();
        var from = new DateTimeOffset(2026, 10, 11, 1, 0, 0, Offset); // 01:00, between 22:00 Simpsons and 20:00 TNG
        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5").Build(from, from.AddHours(1));

        Assert.Equal("Simpsons", r.Programmes[0].Title);
    }
}
