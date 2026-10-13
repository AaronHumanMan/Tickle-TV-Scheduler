using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;
using TickleTV.Scheduler.Core.Playout;

namespace TickleTV.Scheduler.Tests;

/// <summary>Holiday commercials per slot: mixed in, only, or following the calendar's holiday for the day.</summary>
public class HolidayAdTests : IDisposable
{
    private const long Min = 60_000;
    private readonly CatalogDb _db = CatalogDb.InMemory();
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

    public void Dispose() => _db.Dispose();

    private long AddItem(string path, MediaKind kind, long durationMs, params (long start, long end)[] chapters)
    {
        long id = _db.Insert(new MediaItem
        {
            ContentHash = Guid.NewGuid().ToString("N"), Path = path, FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch,
            Kind = kind, KindSource = FactSource.Filename, DurationMs = durationMs, ProbedUtc = DateTimeOffset.UnixEpoch,
            Title = Path.GetFileNameWithoutExtension(path),
        });
        _db.ReplaceChapters(id, chapters.Select((c, i) => new CatalogChapter(i, c.start, c.end, null)));
        return id;
    }

    private void SeedShow()
    {
        for (int i = 1; i <= 8; i++)
        {
            long id = AddItem($"/tv/toons/e{i}.mkv", MediaKind.Episode, 22 * Min, (0, 7 * Min), (7 * Min, 15 * Min), (15 * Min, 22 * Min));
            _db.UpsertEpisode(id, new EpisodeInfo { Show = "Toons", Season = 1, Episode = i, Source = FactSource.Filename });
        }
    }

    private void SeedAds(string prefix, int count, string? holiday = null)
    {
        for (int i = 0; i < count; i++)
        {
            long id = AddItem($"/ads/{prefix}{i}.mp4", MediaKind.Commercial, 30_000 + (i % 3) * 15_000);
            if (holiday is not null) _db.AddTag(id, "holiday:" + holiday, "filename");
        }
    }

    private static ChannelConfig Channel(SlotAdsConfig ads, List<CalendarRule>? calendar = null)
    {
        var ch = new ChannelConfig
        {
            Number = "1",
            TvgId = "t.1",
            Defaults = new SlotDefaults { Pad = "30", MaxBreaks = 2, MinActSeconds = 120, PostRoll = true },
            Ads = new AdsConfig { Pool = "kind = commercial", CooldownMinutes = 10, MaxRepeatsPerBreak = 1 },
            Calendar = calendar ?? [],
        };
        if (calendar is null)
            ch.Schedule = [new SlotConfig { Time = "06:00", Show = "Toons", Order = "chronological", Ads = ads }];
        else
            ch.Plans =
            [
                new DayPlan { Name = "Normal", Slots = [new SlotConfig { Time = "06:00", Show = "Toons", Ads = ads }] },
                new DayPlan { Name = "Christmas", Slots = [new SlotConfig { Time = "06:00", Show = "Toons", Ads = ads }] },
            ];
        return ch;
    }

    private static List<string> AdPaths(BuildResult r) =>
        r.Playout.Items.Where(i => i.Id.StartsWith("ad-", StringComparison.Ordinal)).Select(i => (i.Source as LocalSource)?.Path ?? "").ToList();

    private BuildResult Build(ChannelConfig ch, DateTimeOffset from)
    {
        new SchedulerConfig { Channels = [ch] }.Validate();
        return new ChannelBuilder(_db, new EpisodePicker(_db, seed: 7), ch, "0.0.5") { TimeZone = null }.Build(from, from.AddHours(2));
    }

    private static readonly DateTimeOffset OrdinaryDay = new(2026, 10, 12, 6, 0, 0, Offset);

    [Fact]
    public void Only_mode_airs_nothing_but_the_holidays_commercials()
    {
        SeedShow();
        SeedAds("xmas", 30, "christmas");
        SeedAds("plain", 60);

        BuildResult r = Build(Channel(new SlotAdsConfig { Holiday = "christmas", HolidayAds = "only" }), OrdinaryDay);

        var ads = AdPaths(r);
        Assert.NotEmpty(ads);
        Assert.All(ads, p => Assert.Contains("/ads/xmas", p));
    }

    [Fact]
    public void Mix_mode_puts_holiday_commercials_in_every_break_alongside_the_rest()
    {
        SeedShow();
        SeedAds("xmas", 40, "christmas");
        SeedAds("plain", 400);

        BuildResult r = Build(Channel(new SlotAdsConfig { Holiday = "christmas" }), OrdinaryDay);

        var ads = AdPaths(r);
        int xmas = ads.Count(p => p.Contains("/ads/xmas"));
        Assert.True(xmas > 0, "no holiday commercial aired");
        Assert.True(xmas < ads.Count, "nothing but holiday commercials aired in mix mode");
        Assert.True(xmas >= ads.Count / 4, $"only {xmas} of {ads.Count} ads were seasonal");
    }

    [Fact]
    public void Only_mode_falls_back_to_other_commercials_when_the_holiday_runs_short_and_says_so()
    {
        SeedShow();
        SeedAds("xmas", 2, "christmas");
        SeedAds("plain", 60);

        BuildResult r = Build(Channel(new SlotAdsConfig { Holiday = "christmas", HolidayAds = "only" }), OrdinaryDay);

        Assert.Contains(AdPaths(r), p => p.Contains("/ads/plain"));
        Assert.Contains(r.Warnings, w => w.Contains("Christmas Day commercials were free", StringComparison.Ordinal));
    }

    [Fact]
    public void Calendar_mode_follows_the_days_holiday_and_does_nothing_on_an_ordinary_day()
    {
        SeedShow();
        SeedAds("xmas", 30, "christmas");
        SeedAds("plain", 60);
        var calendar = new List<CalendarRule> { new() { Plan = "Christmas", Holiday = "christmas", DaysBefore = 6 } };

        BuildResult christmasEve = Build(Channel(new SlotAdsConfig { Holiday = "calendar", HolidayAds = "only" }, calendar), new DateTimeOffset(2026, 12, 24, 6, 0, 0, Offset));
        Assert.All(AdPaths(christmasEve), p => Assert.Contains("/ads/xmas", p));

        BuildResult october = Build(Channel(new SlotAdsConfig { Holiday = "calendar", HolidayAds = "only" }, calendar), OrdinaryDay);
        Assert.Contains(AdPaths(october), p => p.Contains("/ads/plain"));
        Assert.DoesNotContain(october.Warnings, w => w.Contains("commercials were free", StringComparison.Ordinal));
    }

    [Fact]
    public void Config_normalises_and_round_trips_the_holiday_rule()
    {
        var mix = new SlotAdsConfig { Holiday = "Christmas-Eve" };
        mix.Normalize();
        Assert.Equal("christmas", mix.Holiday);
        Assert.Null(mix.HolidayAds);
        Assert.Equal(HolidayAdsMode.Mix, mix.HolidayAdsMode);
        Assert.False(mix.IsEmpty);

        var only = new SlotAdsConfig { Holiday = "calendar", HolidayAds = "only" };
        only.Normalize();
        Assert.Equal("calendar", only.Holiday);
        Assert.Equal("only", only.HolidayAds);

        var none = new SlotAdsConfig { HolidayAds = "only" };
        none.Normalize();
        Assert.True(none.IsEmpty);

        Assert.Throws<InvalidDataException>(() => new SlotAdsConfig { Holiday = "arbor-day" }.Normalize());
        Assert.Throws<InvalidDataException>(() => new SlotAdsConfig { Holiday = "christmas", HolidayAds = "sometimes" }.Normalize());

        string dir = Path.Combine(Path.GetTempPath(), "tickletv-hol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfg = new SchedulerConfig
            {
                Catalog = "tickletv.sqlite",
                Next = new NextConfig { Lineup = @"C:\Next\config\lineup.json", SchemaVersion = "0.0.5" },
                Channels = [new ChannelConfig { Number = "1", TvgId = "t.1", Schedule = [new SlotConfig { Time = "06:00", Show = "Toons", Ads = new SlotAdsConfig { Holiday = "calendar", HolidayAds = "only" } }] }],
            };
            cfg.Validate();
            string path = Path.Combine(dir, "tickletv.yaml");
            cfg.Save(path);
            string yaml = File.ReadAllText(path);
            Assert.Contains("holiday: calendar", yaml);
            Assert.Contains("holiday_ads: only", yaml);
            var back = SchedulerConfig.Load(path).Channels.Single().Schedule.Single().Ads!;
            Assert.Equal(HolidayAdsMode.Only, back.HolidayAdsMode);
            Assert.Equal("calendar", back.Holiday);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Count_of_tagged_commercials_per_holiday()
    {
        SeedAds("xmas", 5, "christmas");
        SeedAds("boo", 2, "halloween");
        SeedAds("plain", 3);
        var counts = _db.CountHolidayCommercials();
        Assert.Equal(5, counts["christmas"]);
        Assert.Equal(2, counts["halloween"]);
        Assert.Equal(2, counts.Count);
    }
}
