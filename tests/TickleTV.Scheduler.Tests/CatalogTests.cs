using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Media;

namespace TickleTV.Scheduler.Tests;

/// <summary>Scanner and catalog tests run against real temp files with a stub prober, so no ffprobe is needed.</summary>
public class CatalogTests : IDisposable
{
    private readonly string _root;
    private readonly CatalogDb _db;

    private sealed class StubProber : IMediaProber
    {
        public int Calls;
        public Task<MediaProbe> ProbeAsync(string path, CancellationToken ct = default)
        {
            Calls++;
            var chapters = path.Contains("TNG")
                ? new List<Chapter> { new(0, 300_000, "Chapter 1"), new(300_000, 900_000, "Chapter 2"), new(900_000, 1_500_000, "Chapter 3") }
                : new List<Chapter>();
            return Task.FromResult(new MediaProbe
            {
                Path = path,
                DurationMs = path.Contains("Commercial") ? 30_000 : 1_500_000,
                Chapters = chapters,
                Streams = [new MediaStreamInfo(0, "video", "h264"), new MediaStreamInfo(1, "audio", "aac")],
                Width = 1920,
                Height = 1080,
            });
        }
    }

    public CatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tickletv-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _db = new CatalogDb(Path.Combine(_root, "catalog.sqlite"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string MakeFile(string relative, string content)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public async Task Scan_adds_files_with_parsed_facts_chapters_and_auto_tags()
    {
        MakeFile(@"Shows\TNG\Season 1\257 - 1990-10-15 - TNG S04 E04 - Family.mkv", "tng-family");
        MakeFile(@"Shows\The Simpsons\Season 1\S05E02 Cape Feare.mkv", "simpsons-cape-feare");
        MakeFile(@"Commercials\Taco Bell MC Hammer Commercial 1991 (480p).mp4", "taco");
        MakeFile(@"notes.txt", "ignored");

        var prober = new StubProber();
        ScanReport report = await new Scanner(_db, prober).ScanAsync([_root]);

        Assert.Equal(3, report.Added);
        Assert.Equal(3, prober.Calls);
        Assert.Empty(report.Unclassified);

        var shows = _db.ListShows();
        Assert.Equal(["TNG", "The Simpsons"], shows.OrderBy(s => s, StringComparer.Ordinal).ToArray());

        CatalogEntry tng = _db.EpisodesOf("TNG").Single();
        Assert.Equal(3, tng.Chapters.Count);
        Assert.Equal(new DateOnly(1990, 10, 15), tng.Episode!.AirDate);
        Assert.Contains(tng.Tags, t => t.Name == "1990s");
        Assert.Contains(tng.Tags, t => t.Name == "show:tng");

        MediaItem ad = _db.ListItems(MediaKind.Commercial).Single();
        Assert.Equal(1991, ad.Year);
        Assert.Equal(30_000, ad.DurationMs);
    }

    [Fact]
    public async Task Second_scan_probes_nothing_when_files_are_unchanged()
    {
        MakeFile(@"Commercials\Prell Commercial 1988.mp4", "prell");
        var prober = new StubProber();
        var scanner = new Scanner(_db, prober);

        await scanner.ScanAsync([_root]);
        ScanReport second = await scanner.ScanAsync([_root]);

        Assert.Equal(1, prober.Calls);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(0, second.Added);
    }

    [Fact]
    public async Task Moved_file_keeps_its_identity_tags_and_plays()
    {
        string original = MakeFile(@"Commercials\Prell Commercial 1988.mp4", "prell-bytes");
        var scanner = new Scanner(_db, new StubProber());
        await scanner.ScanAsync([_root]);

        MediaItem before = _db.ListItems().Single();
        _db.AddTag(before.Id, "kids", FactSource.Manual);
        _db.RecordPlay(before.Id, "1", DateTimeOffset.UtcNow, "break 1");

        string moved = Path.Combine(_root, "Ads", "Renamed Spot.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(original, moved);

        ScanReport report = await scanner.ScanAsync([_root]);

        Assert.Equal(1, report.Moved);
        Assert.Equal(0, report.Added);
        MediaItem after = _db.ListItems().Single();
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(moved, after.Path);
        Assert.Contains(_db.GetTags(after.Id), t => t.Name == "kids");
        Assert.NotNull(_db.LastPlayed(after.Id));
    }

    [Fact]
    public async Task Deleted_file_is_marked_missing_not_dropped()
    {
        string file = MakeFile(@"Commercials\Prell Commercial 1988.mp4", "prell");
        var scanner = new Scanner(_db, new StubProber());
        await scanner.ScanAsync([_root]);
        File.Delete(file);

        ScanReport report = await scanner.ScanAsync([_root]);

        Assert.Equal(1, report.Missing);
        Assert.Empty(_db.ListItems());
        Assert.Single(_db.ListItems(includeMissing: true));
    }

    [Fact]
    public async Task Manual_kind_and_tags_survive_a_rescan_of_a_changed_file()
    {
        string file = MakeFile(@"misc\clip.mp4", "v1");
        var scanner = new Scanner(_db, new StubProber());
        ScanReport first = await scanner.ScanAsync([_root]);
        Assert.Single(first.Unclassified);

        MediaItem item = _db.ListItems().Single();
        _db.SetKind(item.Id, MediaKind.Bumper, FactSource.Manual);
        _db.AddTag(item.Id, "station-id", FactSource.Manual);

        File.WriteAllText(file, "v2 with different bytes");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));
        ScanReport second = await scanner.ScanAsync([_root]);

        Assert.Equal(1, second.Updated);
        Assert.Empty(second.Unclassified);
        MediaItem after = _db.FindById(item.Id)!;
        Assert.Equal(MediaKind.Bumper, after.Kind);
        Assert.Contains(_db.GetTags(item.Id), t => t.Name == "station-id" && t.Source == FactSource.Manual);
    }

    [Fact]
    public async Task Episodes_are_ordered_by_air_date_then_season_and_episode()
    {
        MakeFile(@"Shows\TNG\Season 1\274 - 1991-04-01 - TNG S04 E19 - The Nth Degree.mkv", "a");
        MakeFile(@"Shows\TNG\Season 1\257 - 1990-10-15 - TNG S04 E04 - Family.mkv", "b");
        MakeFile(@"Shows\TNG\Season 1\258 - 1990-10-08 - TNG S04 E03 - Brothers.mkv", "c");
        await new Scanner(_db, new StubProber()).ScanAsync([_root]);

        var eps = _db.EpisodesOf("tng"); // case-insensitive lookup
        Assert.Equal([3, 4, 19], eps.Select(e => e.Episode!.Episode!.Value).ToArray());
    }

    [Fact]
    public void Tag_names_are_normalized()
    {
        Assert.Equal("taco-bell", CatalogDb.NormalizeTag("  Taco Bell "));
    }
}
