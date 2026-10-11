using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Tests;

/// <summary>Slots start on time: leftover minutes become a station break, and padding never crosses the next slot.</summary>
public class HoldGridTests : IDisposable
{
    private const long Min = 60_000;
    private readonly CatalogDb _db = CatalogDb.InMemory();
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

    public void Dispose() => _db.Dispose();

    private void Seed()
    {
        // 22:00-long episodes with no chapters: each runs straight through and pads to the next 5 minutes (25 min blocks).
        for (int i = 1; i <= 6; i++)
        {
            long id = _db.Insert(new MediaItem
            {
                ContentHash = $"h{i}", Path = $"/tv/short/e{i}.mkv", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch,
                Kind = MediaKind.Episode, KindSource = FactSource.Filename, DurationMs = 22 * Min, ProbedUtc = DateTimeOffset.UnixEpoch, Title = $"e{i}",
            });
            _db.UpsertEpisode(id, new EpisodeInfo { Show = "Short", Season = 1, Episode = i, Source = FactSource.Filename });
        }
        long doc = _db.Insert(new MediaItem
        {
            ContentHash = "doc", Path = "/tv/doc/e1.mkv", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch,
            Kind = MediaKind.Episode, KindSource = FactSource.Filename, DurationMs = 44 * Min, ProbedUtc = DateTimeOffset.UnixEpoch, Title = "doc",
        });
        _db.UpsertEpisode(doc, new EpisodeInfo { Show = "Doc", Season = 1, Episode = 1, Source = FactSource.Filename });

        for (int i = 0; i < 12; i++)
            _db.Insert(new MediaItem { ContentHash = $"a{i}", Path = $"/ads/{i}.mp4", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch, Kind = MediaKind.Commercial, KindSource = FactSource.Filename, DurationMs = 30_000 + i * 5_000, ProbedUtc = DateTimeOffset.UnixEpoch });
        for (int i = 0; i < 4; i++)
        {
            long b = _db.Insert(new MediaItem { ContentHash = $"p{i}", Path = $"/promos/{i}.mp4", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch, Kind = MediaKind.Bumper, KindSource = FactSource.Folder, DurationMs = 20_000 + i * 7_000, ProbedUtc = DateTimeOffset.UnixEpoch });
            _db.AddTag(b, "promo", FactSource.Filename);
        }
    }

    private static ChannelConfig Channel() => new()
    {
        Number = "1",
        Defaults = new SlotDefaults { Pad = "30", PostRoll = true },
        Gap = new GapConfig { HoldGrid = true, MaxMinutes = 12 },
        Schedule =
        [
            new SlotConfig { Time = "06:00", Show = "Short", Order = "chronological" },
            new SlotConfig { Time = "07:00", Show = "Doc", Order = "chronological", Pad = "60" },
        ],
    };

    [Fact]
    public void Leftover_minutes_become_a_station_break_and_the_next_slot_starts_on_time()
    {
        Seed();
        var from = new DateTimeOffset(2026, 10, 12, 6, 0, 0, Offset);
        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5").Build(from, from.AddHours(2));

        // 06:00-06:25 and 06:25-06:50 air; a third 22-minute episode cannot finish by 07:00, so 06:50-07:00 is a break.
        Assert.Equal("Short", r.Programmes[0].Title);
        Assert.Equal("Short", r.Programmes[1].Title);
        Assert.Equal(from.AddHours(1), r.Programmes[1].Stop); // the station break is folded into the last programme
        Assert.Equal("Doc", r.Programmes[2].Title);
        Assert.Equal(from.AddHours(1), r.Programmes[2].Start);

        // The break holds only promos, ads, bumpers, or filler, never an act.
        var breakItems = r.Playout.Items.Where(i => i.Start >= from.AddMinutes(50) && i.Start < from.AddHours(1)).ToList();
        Assert.NotEmpty(breakItems);
        Assert.DoesNotContain(breakItems, i => i.Id.StartsWith("act-"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("past the next slot"));
    }

    [Fact]
    public void An_episode_that_fits_is_padded_only_up_to_the_next_slot()
    {
        Seed();
        ChannelConfig ch = Channel();
        ch.Schedule[1] = new SlotConfig { Time = "06:48", Show = "Doc", Order = "chronological", Pad = "60" };
        var from = new DateTimeOffset(2026, 10, 12, 6, 0, 0, Offset);
        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), ch, "0.0.5").Build(from, from.AddMinutes(50));

        // Second Short episode: 06:25 + 22:00 = 06:47, which fits before 06:48; it pads to 06:48, not to 06:50.
        Assert.Equal(from.AddMinutes(48), r.Programmes[1].Stop);
        Assert.Equal(from.AddMinutes(48), r.Programmes[2].Start);
    }

    [Fact]
    public void A_long_gap_airs_the_episode_and_drifts_with_a_warning()
    {
        Seed();
        ChannelConfig ch = Channel();
        ch.Gap = new GapConfig { HoldGrid = true, MaxMinutes = 5 };
        var from = new DateTimeOffset(2026, 10, 12, 6, 0, 0, Offset);
        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), ch, "0.0.5").Build(from, from.AddHours(2));

        // A 10-minute gap exceeds max_minutes 5, so the third episode airs and Doc starts late.
        Assert.Equal("Short", r.Programmes[2].Title);
        Assert.Contains(r.Warnings, w => w.Contains("past the next slot"));
    }
}
