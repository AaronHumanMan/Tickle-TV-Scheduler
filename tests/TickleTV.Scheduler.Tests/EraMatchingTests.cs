using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;
using TickleTV.Scheduler.Core.Playout;

namespace TickleTV.Scheduler.Tests;

/// <summary>Commercials by year per slot: around the airing episode's year, or from fixed decades, with the usual fallback when they run short.</summary>
public class EraMatchingTests : IDisposable
{
    private const long Min = 60_000;
    private readonly CatalogDb _db = CatalogDb.InMemory();
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

    public void Dispose() => _db.Dispose();

    private long AddItem(string path, MediaKind kind, long durationMs, int? year, params (long start, long end)[] chapters)
    {
        long id = _db.Insert(new MediaItem
        {
            ContentHash = Guid.NewGuid().ToString("N"), Path = path, FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch,
            Kind = kind, KindSource = FactSource.Filename, Year = year, DurationMs = durationMs, ProbedUtc = DateTimeOffset.UnixEpoch,
            Title = Path.GetFileNameWithoutExtension(path),
        });
        _db.ReplaceChapters(id, chapters.Select((c, i) => new CatalogChapter(i, c.start, c.end, null)));
        return id;
    }

    private void SeedShow(DateOnly? airDate, int? fileYear = null)
    {
        for (int i = 1; i <= 6; i++)
        {
            long id = AddItem($"/tv/toons/e{i}.mkv", MediaKind.Episode, 22 * Min, fileYear, (0, 7 * Min), (7 * Min, 15 * Min), (15 * Min, 22 * Min));
            _db.UpsertEpisode(id, new EpisodeInfo { Show = "Toons", Season = 1, Episode = i, AirDate = airDate?.AddDays(i), Source = FactSource.Filename });
        }
    }

    private void SeedAds(string prefix, int count, int? year, string? decadeTag = null, string? category = null)
    {
        for (int i = 0; i < count; i++)
        {
            long id = AddItem($"/ads/{prefix}{i}.mp4", MediaKind.Commercial, 30_000 + (i % 3) * 15_000, year);
            if (decadeTag is not null) _db.AddTag(id, decadeTag, "folder");
            if (category is not null) _db.AddTag(id, "category:" + category, "folder");
        }
    }

    private static ChannelConfig Channel(SlotAdsConfig? slotAds) => new()
    {
        Number = "1",
        TvgId = "t.1",
        Defaults = new SlotDefaults { Pad = "30", MaxBreaks = 2, MinActSeconds = 120, PostRoll = true },
        Ads = new AdsConfig { Pool = "kind = commercial", CooldownMinutes = 10, MaxRepeatsPerBreak = 1 },
        Schedule = [new SlotConfig { Time = "06:00", Show = "Toons", Order = "chronological", Ads = slotAds }],
    };

    private static List<string> AdPaths(BuildResult r) =>
        r.Playout.Items.Where(i => i.Id.StartsWith("ad-", StringComparison.Ordinal)).Select(i => (i.Source as LocalSource)?.Path ?? "").ToList();

    private BuildResult Build(ChannelConfig ch)
    {
        var from = new DateTimeOffset(2026, 10, 11, 6, 0, 0, Offset);
        return new ChannelBuilder(_db, new EpisodePicker(_db, seed: 7), ch, "0.0.5").Build(from, from.AddHours(2));
    }

    private static CatalogEntry Clip(int? year, params string[] tags) => new()
    {
        Item = new MediaItem { ContentHash = "x", Path = "/ads/x.mp4", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch, Kind = MediaKind.Commercial, KindSource = FactSource.Filename, Year = year, DurationMs = 30_000, ProbedUtc = DateTimeOffset.UnixEpoch },
        Chapters = [],
        Tags = tags.Select(t => new Tag(t, "folder")).ToList(),
    };

    [Theory]
    [InlineData("80s", "1980s")]
    [InlineData("1990s", "1990s")]
    [InlineData("2000", "2000s")]
    [InlineData(" 70's ", "1970s")]
    [InlineData("20s", "2020s")]
    [InlineData("cars", null)]
    [InlineData("1985s", "1980s")]
    public void Decade_text_becomes_a_decade_tag(string text, string? expected) =>
        Assert.Equal(expected, AdRules.DecadeTag(text));

    [Fact]
    public void A_clip_is_in_a_decade_by_year_first_then_by_tag()
    {
        Assert.True(AdRules.InDecades(Clip(1985), ["1980s"]));
        Assert.False(AdRules.InDecades(Clip(1985), ["1990s"]));
        Assert.True(AdRules.InDecades(Clip(null, "1990s", "category:cars"), ["1990s"]));
        Assert.False(AdRules.InDecades(Clip(null, "1990s"), ["1980s"]));
        Assert.False(AdRules.InDecades(Clip(null, "category:cars"), ["1980s", "1990s"]), "no year and no decade: not in any decade");
        Assert.True(AdRules.InDecades(Clip(1995, "1980s"), ["1990s"]), "a known year beats a decade tag");
    }

    [Fact]
    public void A_clip_is_in_the_shows_era_within_the_span()
    {
        var rule = new SlotAdsConfig { Era = "show", EraWithin = 3 };
        Assert.True(AdRules.InShowEra(Clip(1988), rule, 1991));
        Assert.False(AdRules.InShowEra(Clip(1987), rule, 1991));
        Assert.True(AdRules.InShowEra(Clip(1994), rule, 1991));
        Assert.True(AdRules.InShowEra(Clip(null, "1980s"), rule, 1991), "a 1980s clip could be from 1989, which is within 3 years of 1991");
        Assert.False(AdRules.InShowEra(Clip(null, "1980s"), rule, 1995));
        Assert.False(AdRules.InShowEra(Clip(null), rule, 1991), "nothing says when it was made");
        Assert.True(AdRules.InShowEra(Clip(1950), rule, null), "no episode year: nothing to match, everything passes");
    }

    [Fact]
    public void Normalize_tidies_the_year_rule()
    {
        var decades = new SlotAdsConfig { Era = "decades", Decades = ["90s", "1980s", "80s", "junk"] };
        decades.Normalize();
        Assert.Equal(["1980s", "1990s"], decades.Decades);
        Assert.Equal(EraMode.Decades, decades.EraMode);
        Assert.False(decades.IsEmpty);

        var nothingTicked = new SlotAdsConfig { Era = "decades" };
        nothingTicked.Normalize();
        Assert.Null(nothingTicked.Era);
        Assert.True(nothingTicked.IsEmpty);

        var show = new SlotAdsConfig { Era = "Show", EraWithin = 3, Decades = ["1980s"] };
        show.Normalize();
        Assert.Equal("show", show.Era);
        Assert.Null(show.EraWithin);
        Assert.Empty(show.Decades);
        Assert.Equal(3, show.EraSpan);

        var wide = new SlotAdsConfig { Era = "show", EraWithin = 7 };
        wide.Normalize();
        Assert.Equal(7, wide.EraWithin);

        Assert.Throws<InvalidDataException>(() => new SlotAdsConfig { Era = "sometime" }.Normalize());
    }

    [Fact]
    public void Year_rules_survive_save_and_load()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tickletv-era-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "tickletv.yaml");
        try
        {
            var cfg = new SchedulerConfig
            {
                Catalog = "tickletv.sqlite",
                Next = new NextConfig { Lineup = @"C:\Next\config\lineup.json", SchemaVersion = "0.0.5" },
                Channels =
                [
                    new ChannelConfig
                    {
                        Number = "1", TvgId = "t.1",
                        Schedule =
                        [
                            new SlotConfig { Time = "06:00", Show = "Toons", Ads = new SlotAdsConfig { Era = "show", EraWithin = 5 } },
                            new SlotConfig { Time = "20:00", Show = "TNG", Ads = new SlotAdsConfig { Only = ["cars"], Era = "decades", Decades = ["80s"] } },
                        ],
                    },
                ],
            };
            cfg.Validate();
            cfg.Save(path);
            string yaml = File.ReadAllText(path);
            Assert.Contains("era: show", yaml);
            Assert.Contains("era_within: 5", yaml);
            Assert.Contains("- 1980s", yaml);
            Assert.DoesNotContain("era_mode", yaml);
            Assert.DoesNotContain("era_span", yaml);

            SchedulerConfig back = SchedulerConfig.Load(path);
            var slots = back.Channels.Single().Schedule;
            Assert.Equal(EraMode.Show, slots[0].Ads!.EraMode);
            Assert.Equal(5, slots[0].Ads!.EraSpan);
            Assert.Equal(EraMode.Decades, slots[1].Ads!.EraMode);
            Assert.Equal(["1980s"], slots[1].Ads!.Decades);
            Assert.Equal(["cars"], slots[1].Ads!.Only);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_show_era_slot_airs_commercials_from_around_the_episodes_year()
    {
        SeedShow(new DateOnly(1991, 9, 1));
        SeedAds("old", 20, 1975);
        SeedAds("right", 20, 1990);
        SeedAds("tagged", 10, null, "1990s");
        SeedAds("undated", 20, null);

        BuildResult r = Build(Channel(new SlotAdsConfig { Era = "show" }));

        var ads = AdPaths(r);
        Assert.NotEmpty(ads);
        Assert.All(ads, p => Assert.True(p.Contains("/ads/right") || p.Contains("/ads/tagged"), p));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("any year may air", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_show_era_slot_widens_to_any_year_when_the_era_runs_short_and_says_so()
    {
        SeedShow(new DateOnly(1991, 9, 1));
        SeedAds("right", 2, 1990);
        SeedAds("old", 20, 1975);

        BuildResult r = Build(Channel(new SlotAdsConfig { Era = "show" }));

        var ads = AdPaths(r);
        Assert.Contains(ads, p => p.Contains("/ads/old"));
        Assert.Contains(r.Warnings, w => w.Contains("from around 1991", StringComparison.Ordinal) && w.Contains("any year may air", StringComparison.Ordinal));
    }

    [Fact]
    public void A_show_with_no_dates_takes_any_year_and_the_build_explains_once()
    {
        SeedShow(airDate: null);
        SeedAds("old", 20, 1975);

        BuildResult r = Build(Channel(new SlotAdsConfig { Era = "show" }));

        Assert.NotEmpty(AdPaths(r));
        Assert.Single(r.Warnings, w => w.Contains("no year or air date", StringComparison.Ordinal));
    }

    [Fact]
    public void A_decades_slot_airs_only_those_decades_and_counts_match()
    {
        SeedShow(new DateOnly(2005, 1, 1));
        SeedAds("eighties", 20, null, "1980s");
        SeedAds("nineties", 20, 1994);
        SeedAds("undated", 20, null);

        BuildResult r = Build(Channel(new SlotAdsConfig { Era = "decades", Decades = ["1980s"] }));

        var ads = AdPaths(r);
        Assert.NotEmpty(ads);
        Assert.All(ads, p => Assert.Contains("/ads/eighties", p));
        Assert.Equal(20, _db.CountCommercials([], [], ["1980s"]));
        Assert.Equal(40, _db.CountCommercials([], [], ["1980s", "1990s"]));
        Assert.Equal(40, _db.CountCommercials([], [], datedOnly: true));
        Assert.Equal([new CatalogDb.Decade("1980s", 20), new CatalogDb.Decade("1990s", 20)], _db.ListDecades());
    }

    [Fact]
    public void A_decades_slot_falls_back_to_any_commercial_when_its_decades_run_out()
    {
        SeedShow(new DateOnly(2005, 1, 1));
        SeedAds("eighties", 2, 1985);
        SeedAds("undated", 20, null);

        BuildResult r = Build(Channel(new SlotAdsConfig { Era = "decades", Decades = ["1980s"] }));

        Assert.Contains(AdPaths(r), p => p.Contains("/ads/undated"));
        Assert.Contains(r.Warnings, w => w.Contains("match its rules", StringComparison.Ordinal) && w.Contains("from the 1980s", StringComparison.Ordinal));
        Assert.Contains(r.Warnings, w => w.Contains("any commercial may air", StringComparison.Ordinal));
    }

    [Fact]
    public void Years_of_a_show_come_from_air_dates_then_file_years()
    {
        SeedShow(new DateOnly(1991, 9, 1));
        long undated = AddItem("/tv/toons/extra.mkv", MediaKind.Episode, 22 * Min, 1993);
        _db.UpsertEpisode(undated, new EpisodeInfo { Show = "Toons", Season = 2, Episode = 1, Source = FactSource.Filename });
        long nothing = AddItem("/tv/toons/mystery.mkv", MediaKind.Episode, 22 * Min, null);
        _db.UpsertEpisode(nothing, new EpisodeInfo { Show = "Toons", Season = 2, Episode = 2, Source = FactSource.Filename });

        CatalogDb.ShowYears years = _db.YearsOfShow("toons");
        Assert.Equal(1991, years.First);
        Assert.Equal(1993, years.Last);
        Assert.Equal(7, years.Dated);
        Assert.Equal(8, years.Episodes);
    }
}
