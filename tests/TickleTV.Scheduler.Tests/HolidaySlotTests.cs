using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Tests;

/// <summary>A slot that favours holiday episodes: config, the separate holiday rotation, and the builder's picks.</summary>
public class HolidaySlotTests : IDisposable
{
    private const long Min = 60_000;
    private readonly CatalogDb _db = CatalogDb.InMemory();
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Config_reads_the_holiday_choice_and_rejects_nonsense()
    {
        string yaml = """
            next: { lineup: C:\\x\\lineup.json }
            channels:
              - number: "1"
                schedule:
                  - { time: "20:00", show: Cheers, holiday: Christmas, holiday_episodes: first }
                  - { time: "21:00", show: Cheers, holiday: halloween, holiday_episodes: only }
                  - { time: "22:00", show: Cheers }
            """;
        SchedulerConfig cfg = SchedulerConfig.Parse(yaml, Path.GetTempPath());
        var slots = cfg.Channels[0].Schedule;
        Assert.Equal(HolidayMode.First, slots[0].HolidayMode);
        Assert.Equal("christmas", slots[0].Holiday);
        Assert.Equal("Christmas Day episodes first", slots[0].HolidayWords);
        Assert.Equal(HolidayMode.Only, slots[1].HolidayMode);
        Assert.Equal("Halloween episodes only", slots[1].HolidayWords);
        Assert.Equal(HolidayMode.Any, slots[2].HolidayMode);
        Assert.Null(slots[2].HolidayWords);

        Assert.Throws<InvalidDataException>(() => SchedulerConfig.Parse(yaml.Replace("holiday: halloween", "holiday: festivus"), Path.GetTempPath()));
        Assert.Throws<InvalidDataException>(() => SchedulerConfig.Parse(yaml.Replace("holiday_episodes: only", "holiday_episodes: sometimes"), Path.GetTempPath()));

        string path = Path.Combine(Path.GetTempPath(), "tickletv-hol-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            cfg.Save(path);
            string text = File.ReadAllText(path);
            Assert.Equal(2, text.Split("holiday_episodes:").Length - 1);
            Assert.Equal(HolidayMode.First, SchedulerConfig.Load(path).Channels[0].Schedule[0].HolidayMode);
        }
        finally { File.Delete(path); }
    }

    private long AddEpisode(string show, int number, string? holiday = null)
    {
        long id = _db.Insert(new MediaItem
        {
            ContentHash = $"{show}-{number}", Path = $"/tv/{show}/e{number}.mkv", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch,
            Kind = MediaKind.Episode, KindSource = FactSource.Filename, DurationMs = 22 * Min, ProbedUtc = DateTimeOffset.UnixEpoch, Title = $"{show} {number}",
        });
        _db.UpsertEpisode(id, new EpisodeInfo { Show = show, Season = 1, Episode = number, Source = FactSource.Filename });
        if (holiday is not null) _db.AddTag(id, HolidayTagger.TagFor(holiday), FactSource.Filename);
        return id;
    }

    [Fact]
    public void Holiday_picks_have_their_own_rotation_and_first_mode_runs_out_once_a_year()
    {
        for (int i = 1; i <= 6; i++) AddEpisode("Show", i, holiday: i is 2 or 5 ? "christmas" : null);
        var picker = new EpisodePicker(_db, 1);

        // Regular rotation advances to episode 2 next.
        Assert.Equal(1, picker.Next("Show", "1", OrderMode.Chronological)!.Episode!.Episode);

        // "First": the two Christmas episodes once, then null; the regular rotation has not moved.
        Assert.Equal(2, picker.NextHoliday("Show", "1", "christmas", 2026, OrderMode.Chronological, HolidayMode.First)!.Episode!.Episode);
        Assert.Equal(5, picker.NextHoliday("Show", "1", "christmas", 2026, OrderMode.Chronological, HolidayMode.First)!.Episode!.Episode);
        Assert.Null(picker.NextHoliday("Show", "1", "christmas", 2026, OrderMode.Chronological, HolidayMode.First));
        Assert.Null(picker.PeekHolidayDuration("Show", "1", "christmas", 2026, HolidayMode.First));
        Assert.Equal(2, picker.Next("Show", "1", OrderMode.Chronological)!.Episode!.Episode);

        // A new year starts the holiday list over; "only" wraps round for ever.
        Assert.Equal(2, picker.NextHoliday("Show", "1", "christmas", 2027, OrderMode.Chronological, HolidayMode.First)!.Episode!.Episode);
        Assert.Null(picker.NextHoliday("Show", "1", "halloween", 2026, OrderMode.Chronological, HolidayMode.Only));
        var only = Enumerable.Range(0, 5).Select(_ => picker.NextHoliday("Show", "1", "christmas", 2030, OrderMode.Chronological, HolidayMode.Only)!.Episode!.Episode).ToArray();
        Assert.Equal([2, 5, 2, 5, 2], only);

        // Shuffle: both holiday episodes once, then exhausted.
        var shuffled = new[] { picker.NextHoliday("Show", "1", "christmas", 2031, OrderMode.Shuffle, HolidayMode.First)!.Episode!.Episode, picker.NextHoliday("Show", "1", "christmas", 2031, OrderMode.Shuffle, HolidayMode.First)!.Episode!.Episode };
        Assert.Equal([2, 5], shuffled.OrderBy(x => x).ToArray());
        Assert.Null(picker.NextHoliday("Show", "1", "christmas", 2031, OrderMode.Shuffle, HolidayMode.First));
    }

    private void SeedAds()
    {
        for (int i = 0; i < 12; i++)
            _db.Insert(new MediaItem { ContentHash = $"a{i}", Path = $"/ads/{i}.mp4", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch, Kind = MediaKind.Commercial, KindSource = FactSource.Filename, DurationMs = 30_000, ProbedUtc = DateTimeOffset.UnixEpoch });
    }

    [Fact]
    public void A_slot_plays_holiday_episodes_first_then_the_rest_and_only_mode_wraps()
    {
        SeedAds();
        for (int i = 1; i <= 8; i++) AddEpisode("Show", i, holiday: i is 3 or 6 ? "christmas" : null);
        var ch = new ChannelConfig
        {
            Number = "1",
            Defaults = new SlotDefaults { Pad = "30" },
            Schedule = [new SlotConfig { Time = "18:00", Show = "Show", Holiday = "christmas", HolidayEpisodes = "first" }],
        };
        var from = new DateTimeOffset(2026, 12, 20, 18, 0, 0, Offset);
        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), ch, "0.0.5").Build(from, from.AddHours(2));
        // 25-minute blocks: 3, 6 (the Christmas episodes), then regular order from 1.
        Assert.Equal(["S01E03", "S01E06", "S01E01", "S01E02"], r.Programmes.Take(4).Select(p => p.EpisodeNumber!).ToArray());

        ch.Schedule[0].HolidayEpisodes = "only";
        BuildResult only = new ChannelBuilder(_db, new EpisodePicker(_db, 1), ch, "0.0.5").Build(from.AddDays(1), from.AddDays(1).AddHours(2));
        Assert.Equal(["S01E03", "S01E06", "S01E03", "S01E06"], only.Programmes.Take(4).Select(p => p.EpisodeNumber!).ToArray());
        Assert.DoesNotContain(only.Warnings, w => w.Contains("No Christmas"));
    }

    [Fact]
    public void Only_mode_with_no_holiday_episodes_warns_and_hands_the_turn_on()
    {
        SeedAds();
        for (int i = 1; i <= 4; i++) AddEpisode("Plain", i);
        for (int i = 1; i <= 4; i++) AddEpisode("Festive", i, holiday: "halloween");
        var ch = new ChannelConfig
        {
            Number = "1",
            Defaults = new SlotDefaults { Pad = "30" },
            Schedule = [new SlotConfig { Time = "18:00", Shows = ["Plain", "Festive"], Holiday = "halloween", HolidayEpisodes = "only" }],
        };
        var from = new DateTimeOffset(2026, 10, 30, 18, 0, 0, Offset);
        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), ch, "0.0.5").Build(from, from.AddHours(1));
        Assert.All(r.Programmes.Where(p => p.Title is "Plain" or "Festive"), p => Assert.Equal("Festive", p.Title));
        Assert.Contains(r.Warnings, w => w.Contains("No Halloween episodes found for show 'Plain'"));
    }
}
