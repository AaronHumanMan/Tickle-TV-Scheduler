using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Tests;

/// <summary>A slot that lists several shows: config shapes, the one-day capacity note, and turn-taking in the builder.</summary>
public class BlockTests : IDisposable
{
    private const long Min = 60_000;
    private readonly CatalogDb _db = CatalogDb.InMemory();
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Config_accepts_one_show_or_a_list_and_saves_the_list()
    {
        string yaml = """
            next: { lineup: C:\\x\\lineup.json }
            channels:
              - number: "1"
                schedule:
                  - time: "06:00"
                    show: Cheers
                  - time: "08:00"
                    shows: [Frasier, Wings]
                    order: shuffle
            """;
        SchedulerConfig cfg = SchedulerConfig.Parse(yaml, Path.GetTempPath());
        Assert.Equal(["Cheers"], cfg.Channels[0].Schedule[0].ShowList);
        Assert.Equal(["Frasier", "Wings"], cfg.Channels[0].Schedule[1].ShowList);
        Assert.Equal("Frasier / Wings", cfg.Channels[0].Schedule[1].Label);

        string path = Path.Combine(Path.GetTempPath(), "tickletv-block-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            cfg.Save(path);
            string text = File.ReadAllText(path);
            Assert.Contains("- Frasier", text);
            Assert.DoesNotContain("show: ''", text);
            SchedulerConfig back = SchedulerConfig.Load(path);
            Assert.Equal(["Frasier", "Wings"], back.Channels[0].Schedule[1].ShowList);
            Assert.Equal(["Cheers"], back.Channels[0].Schedule[0].ShowList);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Advice_calls_out_a_block_too_short_for_all_of_its_shows()
    {
        var ch = new ChannelConfig
        {
            Number = "1",
            Defaults = new SlotDefaults { Pad = "30" },
            Schedule =
            [
                new SlotConfig { Time = "20:00", Shows = ["Docs A", "Docs B"], Pad = "60" },
                new SlotConfig { Time = "21:00", Shows = ["Sitcom A", "Sitcom B"] },
                new SlotConfig { Time = "22:00", Show = "Late" },
            ],
        };
        long Avg(string show) => show.StartsWith("Docs") ? 45 * Min : 22 * Min;

        Assert.Equal(TimeSpan.FromHours(1), BlockAdvisor.BlockLength(ch, ch.Schedule[0]));
        Assert.Equal(TimeSpan.FromHours(22), BlockAdvisor.BlockLength(ch, ch.Schedule[2]));

        string? docs = BlockAdvisor.Advice(ch, ch.Schedule[0], s => Avg(s));
        Assert.NotNull(docs);
        Assert.Contains("Only one show will play", docs);
        Assert.Contains("Docs A is about 45 min", docs);

        // Two 22-minute shows padded to the half hour both fit in an hour: nothing to say.
        Assert.Null(BlockAdvisor.Advice(ch, ch.Schedule[1], s => Avg(s)));
        Assert.Null(BlockAdvisor.Advice(ch, ch.Schedule[2], s => Avg(s)));
    }

    private void Seed()
    {
        // "Short": 22-minute episodes with no chapters (pad to the next 5 minutes: 25-minute blocks).
        for (int i = 1; i <= 8; i++)
        {
            long id = _db.Insert(new MediaItem
            {
                ContentHash = $"s{i}", Path = $"/tv/short/e{i}.mkv", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch,
                Kind = MediaKind.Episode, KindSource = FactSource.Filename, DurationMs = 22 * Min, ProbedUtc = DateTimeOffset.UnixEpoch, Title = $"s{i}",
            });
            _db.UpsertEpisode(id, new EpisodeInfo { Show = "Short", Season = 1, Episode = i, Source = FactSource.Filename });
        }
        // "Doc": 44-minute episodes with no chapters (45-minute blocks).
        for (int i = 1; i <= 4; i++)
        {
            long id = _db.Insert(new MediaItem
            {
                ContentHash = $"d{i}", Path = $"/tv/doc/e{i}.mkv", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch,
                Kind = MediaKind.Episode, KindSource = FactSource.Filename, DurationMs = 44 * Min, ProbedUtc = DateTimeOffset.UnixEpoch, Title = $"d{i}",
            });
            _db.UpsertEpisode(id, new EpisodeInfo { Show = "Doc", Season = 1, Episode = i, Source = FactSource.Filename });
        }
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
            new SlotConfig { Time = "06:00", Shows = ["Short", "Doc"], Order = "chronological" },
            new SlotConfig { Time = "08:00", Show = "Short", Order = "chronological" },
        ],
    };

    [Fact]
    public void Shows_in_a_block_take_turns_and_a_show_that_will_not_fit_gives_way()
    {
        Seed();
        var from = new DateTimeOffset(2026, 10, 12, 6, 0, 0, Offset);
        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5").Build(from, from.AddHours(2));

        // 06:00 Short (to 06:25), 06:25 Doc (to 07:10), 07:10 Short (to 07:35); Doc cannot finish by 08:00, so Short plays again.
        Assert.Equal(["Short", "Doc", "Short", "Short"], r.Programmes.Take(4).Select(p => p.Title).ToArray());
        Assert.Equal(from.AddMinutes(25), r.Programmes[1].Start);
        Assert.Equal(from.AddHours(2), r.Programmes[3].Stop);
        Assert.Contains(r.Plays, p => p.Context == "slot 06:00 Doc");
        Assert.DoesNotContain(r.Warnings, w => w.Contains("past the next slot"));
    }

    [Fact]
    public void The_turn_carries_over_to_the_next_day()
    {
        Seed();
        var day1 = new DateTimeOffset(2026, 10, 12, 6, 0, 0, Offset);
        BuildResult r1 = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5").Build(day1, day1.AddHours(2));
        foreach (PlayRecord p in r1.Plays) _db.RecordPlay(p.ItemId, "1", p.AiredAt, p.Context);

        // Short closed the block on day 1, so Doc opens it on day 2.
        DateTimeOffset day2 = day1.AddDays(1);
        BuildResult r2 = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5").Build(day2, day2.AddHours(1));
        Assert.Equal("Doc", r2.Programmes[0].Title);
    }

    [Fact]
    public void Config_reads_the_turns_setting_and_rejects_nonsense()
    {
        string yaml = """
            next: { lineup: C:\x\lineup.json }
            channels:
              - number: "1"
                schedule:
                  - time: "06:00"
                    shows: [Frasier, Wings]
                    turns: random
                  - time: "08:00"
                    shows: [Cheers, Taxi]
            """;
        SchedulerConfig cfg = SchedulerConfig.Parse(yaml, Path.GetTempPath());
        Assert.True(cfg.Channels[0].Schedule[0].RandomTurns);
        Assert.False(cfg.Channels[0].Schedule[1].RandomTurns);
        Assert.Throws<InvalidDataException>(() => SchedulerConfig.Parse(yaml.Replace("turns: random", "turns: sideways"), Path.GetTempPath()));

        // The file only carries the line for a random block; the default stays implicit.
        string path = Path.Combine(Path.GetTempPath(), "tickletv-turns-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            cfg.Save(path);
            string text = File.ReadAllText(path);
            Assert.Equal(1, text.Split("turns:").Length - 1);
            Assert.Contains("turns: random", text);
            Assert.True(SchedulerConfig.Load(path).Channels[0].Schedule[0].RandomTurns);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Random_turns_never_play_the_same_show_twice_in_a_row()
    {
        Seed();
        ChannelConfig ch = Channel();
        // Two short shows that both fit anywhere, so only the turn order decides who plays.
        for (int i = 1; i <= 8; i++)
        {
            long id = _db.Insert(new MediaItem
            {
                ContentHash = $"t{i}", Path = $"/tv/tiny/e{i}.mkv", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch,
                Kind = MediaKind.Episode, KindSource = FactSource.Filename, DurationMs = 22 * Min, ProbedUtc = DateTimeOffset.UnixEpoch, Title = $"t{i}",
            });
            _db.UpsertEpisode(id, new EpisodeInfo { Show = "Tiny", Season = 1, Episode = i, Source = FactSource.Filename });
        }
        ch.Schedule = [new SlotConfig { Time = "06:00", Shows = ["Short", "Tiny"], Turns = "random" }, new SlotConfig { Time = "09:00", Show = "Short" }];
        var from = new DateTimeOffset(2026, 10, 12, 6, 0, 0, Offset);

        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), ch, "0.0.5", seed: 7).Build(from, from.AddHours(3));
        string[] titles = r.Programmes.Where(p => p.Title is "Short" or "Tiny").Select(p => p.Title).ToArray();
        Assert.True(titles.Length >= 6, string.Join(",", titles));
        // With two shows, "never twice in a row" means they strictly alternate.
        for (int i = 1; i < titles.Length; i++) Assert.NotEqual(titles[i - 1], titles[i]);
    }

    [Fact]
    public void Random_turns_skip_the_show_that_closed_the_block_yesterday()
    {
        Seed();
        ChannelConfig ch = Channel();
        ch.Schedule[0].Turns = "random";
        var day1 = new DateTimeOffset(2026, 10, 12, 6, 0, 0, Offset);
        // Yesterday's block ended on Short, recorded in the plays table.
        _db.RecordPlay(_db.EpisodesOf("Short")[0].Item.Id, "1", day1.AddDays(-1).AddHours(1), "slot 06:00 Short");

        for (int seed = 0; seed < 10; seed++)
        {
            BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), ch, "0.0.5", seed: seed).Build(day1, day1.AddHours(1));
            Assert.Equal("Doc", r.Programmes[0].Title);
        }
    }

    [Fact]
    public void Build_warns_once_when_a_block_cannot_hold_all_of_its_shows_in_a_day()
    {
        Seed();
        ChannelConfig ch = Channel();
        ch.Schedule = [new SlotConfig { Time = "06:00", Shows = ["Doc", "Short"], Pad = "60" }, new SlotConfig { Time = "07:00", Show = "Short" }];
        var from = new DateTimeOffset(2026, 10, 12, 6, 0, 0, Offset);
        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), ch, "0.0.5").Build(from, from.AddHours(1));
        Assert.Single(r.Warnings, w => w.StartsWith("06:00 block:") && w.Contains("Only one show will play"));
    }
}
