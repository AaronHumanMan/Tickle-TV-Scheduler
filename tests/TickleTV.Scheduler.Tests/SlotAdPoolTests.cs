using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;
using TickleTV.Scheduler.Core.Playout;

namespace TickleTV.Scheduler.Tests;

/// <summary>Commercials by category per slot (R5): a kids slot never pulls a beer ad, the channel can ban a category outright, and a slot whose categories run short falls back with a warning.</summary>
public class SlotAdPoolTests : IDisposable
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
            Kind = kind, KindSource = FactSource.Filename, Year = 1991, DurationMs = durationMs, ProbedUtc = DateTimeOffset.UnixEpoch,
            Title = Path.GetFileNameWithoutExtension(path),
        });
        _db.ReplaceChapters(id, chapters.Select((c, i) => new CatalogChapter(i, c.start, c.end, null)));
        return id;
    }

    private void SeedShow()
    {
        for (int i = 1; i <= 6; i++)
        {
            long id = AddItem($"/tv/toons/e{i}.mkv", MediaKind.Episode, 22 * Min, (0, 7 * Min), (7 * Min, 15 * Min), (15 * Min, 22 * Min));
            _db.UpsertEpisode(id, new EpisodeInfo { Show = "Toons", Season = 1, Episode = i, Source = FactSource.Filename });
        }
    }

    private void SeedAds(string category, int count, string prefix)
    {
        for (int i = 0; i < count; i++)
        {
            long id = AddItem($"/ads/{prefix}{i}.mp4", MediaKind.Commercial, 30_000 + (i % 3) * 15_000);
            if (category.Length > 0) _db.AddTag(id, "category:" + category, "folder");
        }
    }

    private static ChannelConfig Channel(SlotAdsConfig? slotAds = null, List<string>? channelNever = null) => new()
    {
        Number = "1",
        TvgId = "t.1",
        Defaults = new SlotDefaults { Pad = "30", MaxBreaks = 2, MinActSeconds = 120, PostRoll = true },
        Ads = new AdsConfig { Pool = "kind = commercial", CooldownMinutes = 10, MaxRepeatsPerBreak = 1, Never = channelNever ?? [] },
        Schedule = [new SlotConfig { Time = "06:00", Show = "Toons", Order = "chronological", Ads = slotAds }],
    };

    private static IEnumerable<string> AdPaths(BuildResult r) =>
        r.Playout.Items.Where(i => i.Id.StartsWith("ad-", StringComparison.Ordinal)).Select(i => (i.Source as LocalSource)?.Path ?? "");

    private BuildResult Build(ChannelConfig ch)
    {
        var from = new DateTimeOffset(2026, 10, 11, 6, 0, 0, Offset);
        return new ChannelBuilder(_db, new EpisodePicker(_db, seed: 7), ch, "0.0.5").Build(from, from.AddHours(2));
    }

    [Fact]
    public void A_slot_limited_to_toys_never_airs_a_beer_ad()
    {
        SeedShow();
        SeedAds("toys", 20, "toy");
        SeedAds("beer", 20, "beer");

        BuildResult r = Build(Channel(new SlotAdsConfig { Only = ["toys"] }));

        var ads = AdPaths(r).ToList();
        Assert.NotEmpty(ads);
        Assert.All(ads, p => Assert.Contains("/ads/toy", p));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("fall back", StringComparison.OrdinalIgnoreCase) || w.Contains("any commercial may air", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_slot_can_keep_a_category_out_and_still_use_the_rest_including_uncategorised()
    {
        SeedShow();
        SeedAds("beer", 20, "beer");
        SeedAds("toys", 10, "toy");
        SeedAds("", 10, "plain");

        BuildResult r = Build(Channel(new SlotAdsConfig { Never = ["beer"] }));

        var ads = AdPaths(r).ToList();
        Assert.NotEmpty(ads);
        Assert.DoesNotContain(ads, p => p.Contains("/ads/beer"));
        Assert.Contains(ads, p => p.Contains("/ads/plain") || p.Contains("/ads/toy"));
    }

    [Fact]
    public void The_channel_never_list_beats_everything_even_a_slot_that_asks_for_that_category()
    {
        SeedShow();
        SeedAds("beer", 20, "beer");
        SeedAds("toys", 20, "toy");

        BuildResult r = Build(Channel(new SlotAdsConfig { Only = ["beer", "toys"] }, channelNever: ["beer"]));

        var ads = AdPaths(r).ToList();
        Assert.NotEmpty(ads);
        Assert.All(ads, p => Assert.Contains("/ads/toy", p));
    }

    [Fact]
    public void When_the_slots_categories_run_short_breaks_fall_back_to_any_commercial_and_say_so()
    {
        SeedShow();
        SeedAds("toys", 1, "toy");
        SeedAds("beer", 20, "beer");

        BuildResult r = Build(Channel(new SlotAdsConfig { Only = ["toys"] }));

        var ads = AdPaths(r).ToList();
        Assert.Contains(ads, p => p.Contains("/ads/beer"));
        Assert.Contains(r.Warnings, w => w.Contains("only 1 commercials match its categories", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Warnings, w => w.Contains("any commercial may air", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_fallback_still_honours_the_slots_never_list()
    {
        SeedShow();
        SeedAds("toys", 1, "toy");
        SeedAds("beer", 20, "beer");
        SeedAds("food", 20, "food");

        BuildResult r = Build(Channel(new SlotAdsConfig { Only = ["toys"], Never = ["beer"] }));

        var ads = AdPaths(r).ToList();
        Assert.NotEmpty(ads);
        Assert.DoesNotContain(ads, p => p.Contains("/ads/beer"));
        Assert.Contains(ads, p => p.Contains("/ads/food"));
    }

    [Fact]
    public void Category_rules_survive_a_save_and_an_empty_rule_stays_out_of_the_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tickletv-ads-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "tickletv.yaml");
        try
        {
            var cfg = new SchedulerConfig
            {
                Next = new NextConfig { Lineup = @"C:\Next\config\lineup.json" },
                Channels =
                [
                    new ChannelConfig
                    {
                        Number = "1", Name = "Kids",
                        Ads = new AdsConfig { Never = ["Alcohol", "category:tobacco"] },
                        Schedule =
                        [
                            new SlotConfig { Time = "06:00", Show = "Toons", Ads = new SlotAdsConfig { Only = ["Toys", "Breakfast Cereal", "toys"] } },
                            new SlotConfig { Time = "20:00", Show = "Movies", Ads = new SlotAdsConfig() },
                        ],
                    },
                ],
            };
            cfg.Save(path);
            string yaml = File.ReadAllText(path);

            SchedulerConfig back = SchedulerConfig.Load(path);
            ChannelConfig ch = back.Channels.Single();
            Assert.Equal(["alcohol", "tobacco"], ch.Ads.Never);
            SlotConfig morning = ch.Plans[0].Slots.Single(s => s.Time == "06:00");
            Assert.Equal(["toys", "breakfast-cereal"], morning.Ads!.Only);
            Assert.Null(ch.Plans[0].Slots.Single(s => s.Time == "20:00").Ads);
            Assert.Contains("only:", yaml);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Rules_are_described_in_words()
    {
        var rule = new SlotAdsConfig { Only = ["toys", "breakfast-cereal"], Never = ["none"] };
        Assert.Equal("only Toys, Breakfast Cereal; never No category yet", AdRules.Describe(rule));
    }
}
