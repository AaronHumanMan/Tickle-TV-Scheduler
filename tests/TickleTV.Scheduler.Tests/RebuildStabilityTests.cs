using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Tests;

/// <summary>Rebuilding a window must not move every show forward; it reuses what the window already assigned.</summary>
public class RebuildStabilityTests : IDisposable
{
    private const long Min = 60_000;
    private readonly CatalogDb _db = CatalogDb.InMemory();
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

    public void Dispose() => _db.Dispose();

    private void Seed()
    {
        for (int i = 1; i <= 6; i++)
        {
            long id = _db.Insert(new MediaItem
            {
                ContentHash = $"h{i}", Path = $"/tv/tng/e{i}.mkv", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch,
                Kind = MediaKind.Episode, KindSource = FactSource.Filename, DurationMs = 45 * Min, ProbedUtc = DateTimeOffset.UnixEpoch, Title = $"e{i}",
            });
            _db.UpsertEpisode(id, new EpisodeInfo { Show = "TNG", Season = 1, Episode = i, AirDate = new DateOnly(1990, 1, i), Source = FactSource.Filename });
            _db.ReplaceChapters(id, [new CatalogChapter(0, 0, 10 * Min, null), new CatalogChapter(1, 10 * Min, 30 * Min, null), new CatalogChapter(2, 30 * Min, 45 * Min, null)]);
        }
        for (int i = 0; i < 10; i++)
            _db.Insert(new MediaItem { ContentHash = $"a{i}", Path = $"/ads/{i}.mp4", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch, Kind = MediaKind.Commercial, KindSource = FactSource.Filename, DurationMs = 30_000, ProbedUtc = DateTimeOffset.UnixEpoch });
    }

    private static ChannelConfig Channel() => new()
    {
        Number = "1",
        Defaults = new SlotDefaults { Pad = "60", PostRoll = true },
        Schedule = [new SlotConfig { Time = "20:00", Show = "TNG", Order = "chronological" }],
    };

    private static Dictionary<string, Queue<long>> PriorFrom(CatalogDb db, DateTimeOffset since)
    {
        var prior = new Dictionary<string, Queue<long>>(StringComparer.OrdinalIgnoreCase);
        foreach ((long itemId, _, string context) in db.ListSlotPlays("1", since))
        {
            string show = ChannelBuilder.ShowFromSlotContext(context)!;
            if (!prior.TryGetValue(show, out Queue<long>? q)) prior[show] = q = new Queue<long>();
            q.Enqueue(itemId);
        }
        return prior;
    }

    [Fact]
    public void Rebuilding_the_same_window_keeps_the_same_episodes_and_does_not_advance_rotation()
    {
        Seed();
        var from = new DateTimeOffset(2026, 10, 11, 20, 0, 0, Offset);

        BuildResult first = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5").Build(from, from.AddHours(3));
        foreach (PlayRecord p in first.Plays) _db.RecordPlay(p.ItemId, "1", p.AiredAt, p.Context);
        var firstEpisodes = first.Programmes.Select(p => p.EpisodeNumber).ToList();
        Assert.Equal(["S01E01", "S01E02", "S01E03"], firstEpisodes);

        // Rebuild the same window, as the build command does: reuse prior assignments, replace the plays.
        var prior = PriorFrom(_db, from);
        BuildResult second = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5") { PriorAssignments = prior }.Build(from, from.AddHours(3));
        _db.DeletePlays("1", from);
        foreach (PlayRecord p in second.Plays) _db.RecordPlay(p.ItemId, "1", p.AiredAt, p.Context);

        Assert.Equal(firstEpisodes, second.Programmes.Select(p => p.EpisodeNumber).ToList());

        // Rotation did not move: the next window continues with episode 4, not 7-wrapped-to-1 or 5.
        BuildResult next = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5").Build(from.AddHours(3), from.AddHours(4));
        Assert.Equal("S01E04", next.Programmes[0].EpisodeNumber);
    }

    [Fact]
    public void A_longer_rebuild_reuses_the_prefix_then_picks_new_episodes()
    {
        Seed();
        var from = new DateTimeOffset(2026, 10, 11, 20, 0, 0, Offset);
        BuildResult first = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5").Build(from, from.AddHours(2));
        foreach (PlayRecord p in first.Plays) _db.RecordPlay(p.ItemId, "1", p.AiredAt, p.Context);

        BuildResult longer = new ChannelBuilder(_db, new EpisodePicker(_db, 1), Channel(), "0.0.5") { PriorAssignments = PriorFrom(_db, from) }.Build(from, from.AddHours(4));

        Assert.Equal(["S01E01", "S01E02", "S01E03", "S01E04"], longer.Programmes.Select(p => p.EpisodeNumber).ToList());
    }

    [Fact]
    public void Show_is_parsed_from_slot_context_even_with_spaces()
    {
        Assert.Equal("The Simpsons", ChannelBuilder.ShowFromSlotContext("slot 06:00 The Simpsons"));
        Assert.Null(ChannelBuilder.ShowFromSlotContext("ad in TNG"));
    }
}
