using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Tests;

public class EpisodePickerTests : IDisposable
{
    private readonly CatalogDb _db = CatalogDb.InMemory();

    public void Dispose() => _db.Dispose();

    private long AddEpisode(string show, int season, int episode, DateOnly? airDate)
    {
        long id = _db.Insert(new MediaItem
        {
            ContentHash = Guid.NewGuid().ToString("N"), Path = $"/tv/{show}/S{season:00}E{episode:00}.mkv", FileSize = 1,
            ModifiedUtc = DateTimeOffset.UnixEpoch, Kind = MediaKind.Episode, KindSource = FactSource.Filename,
            DurationMs = 1_320_000, ProbedUtc = DateTimeOffset.UnixEpoch,
        });
        _db.UpsertEpisode(id, new EpisodeInfo { Show = show, Season = season, Episode = episode, AirDate = airDate, Source = FactSource.Filename });
        return id;
    }

    [Fact]
    public void Chronological_follows_air_date_and_wraps()
    {
        AddEpisode("TNG", 4, 19, new DateOnly(1991, 4, 1));
        AddEpisode("TNG", 4, 4, new DateOnly(1990, 10, 15));
        AddEpisode("TNG", 4, 3, new DateOnly(1990, 10, 8));
        var picker = new EpisodePicker(_db, seed: 1);

        int[] order = Enumerable.Range(0, 4).Select(_ => picker.Next("TNG", "1", OrderMode.Chronological)!.Episode!.Episode!.Value).ToArray();

        Assert.Equal([3, 4, 19, 3], order);
    }

    [Fact]
    public void Shuffle_airs_every_episode_once_before_repeating()
    {
        for (int i = 1; i <= 10; i++) AddEpisode("Simpsons", 5, i, null);
        var picker = new EpisodePicker(_db, seed: 42);

        var firstCycle = Enumerable.Range(0, 10).Select(_ => picker.Next("Simpsons", "1", OrderMode.Shuffle)!.Item.Id).ToList();
        var secondCycle = Enumerable.Range(0, 10).Select(_ => picker.Next("Simpsons", "1", OrderMode.Shuffle)!.Item.Id).ToList();

        Assert.Equal(10, firstCycle.Distinct().Count());
        Assert.Equal(10, secondCycle.Distinct().Count());
        Assert.NotEqual(Enumerable.Range(1, 10).Select(i => (long)i), firstCycle); // actually shuffled
    }

    [Fact]
    public void Position_survives_a_new_picker_instance()
    {
        AddEpisode("TNG", 1, 1, new DateOnly(1987, 9, 28));
        AddEpisode("TNG", 1, 2, new DateOnly(1987, 10, 5));
        AddEpisode("TNG", 1, 3, new DateOnly(1987, 10, 12));

        Assert.Equal(1, new EpisodePicker(_db).Next("TNG", "1", OrderMode.Chronological)!.Episode!.Episode);
        Assert.Equal(2, new EpisodePicker(_db).Next("TNG", "1", OrderMode.Chronological)!.Episode!.Episode);
    }

    [Fact]
    public void Channels_have_independent_positions()
    {
        AddEpisode("TNG", 1, 1, null);
        AddEpisode("TNG", 1, 2, null);
        var picker = new EpisodePicker(_db);

        Assert.Equal(1, picker.Next("TNG", "1", OrderMode.Chronological)!.Episode!.Episode);
        Assert.Equal(1, picker.Next("TNG", "2", OrderMode.Chronological)!.Episode!.Episode);
        Assert.Equal(2, picker.Next("TNG", "1", OrderMode.Chronological)!.Episode!.Episode);
    }

    [Fact]
    public void Switching_shuffle_to_chronological_resumes_at_the_first_unaired_episode()
    {
        for (int i = 1; i <= 5; i++) AddEpisode("S", 1, i, null);
        var picker = new EpisodePicker(_db, seed: 7);

        var aired = new HashSet<int>();
        for (int i = 0; i < 3; i++) aired.Add(picker.Next("S", "1", OrderMode.Shuffle)!.Episode!.Episode!.Value);

        int resumed = picker.Next("S", "1", OrderMode.Chronological)!.Episode!.Episode!.Value;
        int expected = Enumerable.Range(1, 5).First(e => !aired.Contains(e));
        Assert.Equal(expected, resumed);
    }

    [Fact]
    public void Unknown_show_returns_null()
    {
        Assert.Null(new EpisodePicker(_db).Next("Nope", "1", OrderMode.Shuffle));
    }

    [Fact]
    public void Shuffle_never_plays_the_same_episode_back_to_back_across_cycles()
    {
        for (int i = 1; i <= 4; i++) AddEpisode("Short", 1, i, null);
        for (int seed = 0; seed < 40; seed++)
        {
            using var db = CatalogDb.InMemory();
            for (int i = 1; i <= 4; i++)
            {
                long id = db.Insert(new MediaItem
                {
                    ContentHash = $"s{seed}-{i}", Path = $"/tv/short/{i}.mkv", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch,
                    Kind = MediaKind.Episode, KindSource = FactSource.Filename, DurationMs = 1, ProbedUtc = DateTimeOffset.UnixEpoch,
                });
                db.UpsertEpisode(id, new EpisodeInfo { Show = "Short", Season = 1, Episode = i, Source = FactSource.Filename });
            }
            var picker = new EpisodePicker(db, seed);
            long? previous = null;
            for (int n = 0; n < 40; n++)
            {
                long id = picker.Next("Short", "1", OrderMode.Shuffle)!.Item.Id;
                Assert.NotEqual(previous, id);
                previous = id;
            }
        }
    }
}
