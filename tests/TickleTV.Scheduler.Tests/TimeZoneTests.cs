using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Tests;

/// <summary>Slot times are clock times in the channel's zone: a build that crosses a daylight-saving change keeps 6am at 6am.</summary>
public class TimeZoneTests : IDisposable
{
    private const long Min = 60_000;
    private readonly CatalogDb _db = CatalogDb.InMemory();

    public void Dispose() => _db.Dispose();

    private static TimeZoneInfo Central()
    {
        foreach (string id in new[] { "Central Standard Time", "America/Chicago" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }
        throw new InvalidOperationException("No US Central time zone on this machine.");
    }

    private void Seed()
    {
        for (int i = 1; i <= 6; i++)
        {
            long id = _db.Insert(new MediaItem
            {
                ContentHash = $"h{i}", Path = $"/tv/short/e{i}.mkv", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch,
                Kind = MediaKind.Episode, KindSource = FactSource.Filename, DurationMs = 22 * Min, ProbedUtc = DateTimeOffset.UnixEpoch, Title = $"e{i}",
            });
            _db.UpsertEpisode(id, new EpisodeInfo { Show = "Short", Season = 1, Episode = i, Source = FactSource.Filename });
        }
        for (int i = 0; i < 12; i++)
            _db.Insert(new MediaItem { ContentHash = $"a{i}", Path = $"/ads/{i}.mp4", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch, Kind = MediaKind.Commercial, KindSource = FactSource.Filename, DurationMs = 30_000 + i * 5_000, ProbedUtc = DateTimeOffset.UnixEpoch });
    }

    private static ChannelConfig Channel() => new()
    {
        Number = "1",
        Defaults = new SlotDefaults { Pad = "30", PostRoll = true },
        Gap = new GapConfig { HoldGrid = true, MaxMinutes = 30 },
        Schedule =
        [
            new SlotConfig { Time = "06:00", Show = "Short", Order = "chronological" },
            new SlotConfig { Time = "18:00", Show = "Short", Order = "chronological" },
        ],
    };

    [Fact]
    public void Slots_keep_their_clock_time_after_the_clocks_fall_back()
    {
        Seed();
        TimeZoneInfo central = Central();
        // Saturday 31 Oct 2026, 5pm CDT. Clocks fall back at 2am on Sunday 1 Nov: CDT (-5) becomes CST (-6).
        var from = new DateTimeOffset(2026, 10, 31, 17, 0, 0, TimeSpan.FromHours(-5));
        Assert.True(central.IsDaylightSavingTime(from));

        // 27 hours: 5pm CDT Saturday to 7pm CST Sunday, so Sunday's 6pm slot is inside the window.
        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5") { TimeZone = central }.Build(from, from.AddHours(27));

        var sixAmSunday = new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.FromHours(-6));
        var sixPmSaturday = new DateTimeOffset(2026, 10, 31, 18, 0, 0, TimeSpan.FromHours(-5));
        var sixPmSunday = new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.FromHours(-6));
        Assert.Contains(r.Programmes, p => p.Start == sixPmSaturday);
        Assert.Contains(r.Programmes, p => p.Start == sixAmSunday);
        Assert.Contains(r.Programmes, p => p.Start == sixPmSunday);
        // The old mistake: 6am measured in the summer offset, which is 5am on the wall clock after the change.
        Assert.DoesNotContain(r.Programmes, p => p.Start == sixAmSunday.AddHours(-1));

        // Everything after the change carries the winter offset, so guide and playout files show the right local time.
        Assert.All(r.Programmes.Where(p => p.Start >= sixAmSunday), p => Assert.Equal(TimeSpan.FromHours(-6), p.Start.Offset));
        Assert.All(r.Programmes.Where(p => p.Stop <= from.AddHours(8)), p => Assert.Equal(TimeSpan.FromHours(-5), p.Start.Offset));
    }

    [Fact]
    public void Slots_keep_their_clock_time_after_the_clocks_spring_forward()
    {
        Seed();
        TimeZoneInfo central = Central();
        // Saturday 13 Mar 2027, 5pm CST. Clocks spring forward at 2am on Sunday 14 Mar: CST (-6) becomes CDT (-5).
        var from = new DateTimeOffset(2027, 3, 13, 17, 0, 0, TimeSpan.FromHours(-6));
        Assert.False(central.IsDaylightSavingTime(from));

        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5") { TimeZone = central }.Build(from, from.AddHours(26));

        var sixAmSunday = new DateTimeOffset(2027, 3, 14, 6, 0, 0, TimeSpan.FromHours(-5));
        Assert.Contains(r.Programmes, p => p.Start == sixAmSunday);
        Assert.DoesNotContain(r.Programmes, p => p.Start == sixAmSunday.AddHours(1));
    }

    [Fact]
    public void Without_a_zone_the_builds_own_offset_is_kept_for_the_whole_window()
    {
        Seed();
        var from = new DateTimeOffset(2026, 10, 31, 17, 0, 0, TimeSpan.FromHours(-5));
        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5").Build(from, from.AddHours(26));
        Assert.All(r.Programmes, p => Assert.Equal(TimeSpan.FromHours(-5), p.Start.Offset));
        Assert.Contains(r.Programmes, p => p.Start == new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.FromHours(-5)));
    }
}
