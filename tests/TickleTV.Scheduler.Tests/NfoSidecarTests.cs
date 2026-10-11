using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Media;

namespace TickleTV.Scheduler.Tests;

/// <summary>Kodi-style .nfo sidecars: parsing, merging over the file-name guess, and how the scanner uses them.</summary>
public class NfoSidecarTests : IDisposable
{
    private readonly string _root;
    private readonly CatalogDb _db;

    private sealed class StubProber : IMediaProber
    {
        public int Calls;
        public Task<MediaProbe> ProbeAsync(string path, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new MediaProbe
            {
                Path = path, DurationMs = 1_320_000, Chapters = [],
                Streams = [new MediaStreamInfo(0, "video", "h264"), new MediaStreamInfo(1, "audio", "aac")],
            });
        }
    }

    public NfoSidecarTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tickletv-nfo-" + Guid.NewGuid().ToString("N"));
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

    private const string EpisodeNfo = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <episodedetails>
          <title>The Strange Case of Mr. Lamb</title>
          <showtitle>How It's Made</showtitle>
          <season>10</season>
          <episode>3</episode>
          <aired>2007-11-02</aired>
          <genre>Documentary</genre>
          <genre>Science &amp; Technology</genre>
          <plot>Factories.</plot>
        </episodedetails>
        """;

    [Fact]
    public void Parses_an_episode_sidecar()
    {
        NfoFacts? f = NfoSidecar.Parse(EpisodeNfo);
        Assert.NotNull(f);
        Assert.Equal(MediaKind.Episode, f.Kind);
        Assert.Equal("How It's Made", f.Show);
        Assert.Equal(10, f.Season);
        Assert.Equal(3, f.Episode);
        Assert.Equal("The Strange Case of Mr. Lamb", f.Title);
        Assert.Equal(new DateOnly(2007, 11, 2), f.AirDate);
        Assert.Equal(2007, f.Year);
        Assert.Equal(["Documentary", "Science & Technology"], f.Genres);
    }

    [Fact]
    public void Parses_movies_and_show_files_and_ignores_junk()
    {
        NfoFacts? movie = NfoSidecar.Parse("<movie><title>Spaceballs</title><year>1987</year><genre>Comedy</genre></movie>");
        Assert.Equal(MediaKind.Movie, movie!.Kind);
        Assert.Equal("Spaceballs", movie.Title);
        Assert.Equal(1987, movie.Year);

        NfoFacts? show = NfoSidecar.Parse("<tvshow><title>Good Eats</title><premiered>1999-07-07</premiered><genre>Food</genre></tvshow>");
        Assert.Equal(MediaKind.Unknown, show!.Kind);
        Assert.Equal("Good Eats", show.Show);
        Assert.Equal(["Food"], show.Genres);

        // Kodi writes -1 for unknown numbers, and concatenates several roots for a two-part file: the first wins.
        NfoFacts? multi = NfoSidecar.Parse("<episodedetails><title>Part 1</title><season>-1</season><episode>5</episode></episodedetails>\n<episodedetails><title>Part 2</title><episode>6</episode></episodedetails>");
        Assert.Equal("Part 1", multi!.Title);
        Assert.Null(multi.Season);
        Assert.Equal(5, multi.Episode);

        Assert.Null(NfoSidecar.Parse("https://www.imdb.com/title/tt0094012/"));
        Assert.Null(NfoSidecar.Parse(""));
        Assert.Null(NfoSidecar.Parse("<html><body>not metadata</body></html>"));
        Assert.Null(NfoSidecar.Parse("<episodedetails><title>broken"));
    }

    [Fact]
    public void Sidecar_facts_beat_the_file_name_and_gaps_keep_the_guess()
    {
        // The scene-style name says S10E03 "1080p"; the folder says "How Its Made"; the sidecar has the real show name and title.
        string video = MakeFile(@"Shows\How Its Made\Season 10\How.Its.Made.S10E03.1080p.WEB-DL.mkv", "v");
        MakeFile(@"Shows\How Its Made\Season 10\How.Its.Made.S10E03.1080p.WEB-DL.nfo", EpisodeNfo);

        ParsedName p = Scanner.ParseFacts(video);
        Assert.Equal(MediaKind.Episode, p.Kind);
        Assert.Equal(FactSource.Nfo, p.KindSource);
        Assert.Equal("How It's Made", p.Show);
        Assert.Equal(FactSource.Nfo, p.ShowSource);
        Assert.Equal("The Strange Case of Mr. Lamb", p.EpisodeTitle);
        Assert.Equal(new DateOnly(2007, 11, 2), p.AirDate);
        Assert.Equal(2007, p.Year);
        Assert.Contains("genre:documentary", p.Tags);
        Assert.Contains("genre:science-technology", p.Tags);
        Assert.Contains("2000s", p.Tags);
        Assert.Equal("How It's Made S10E03 The Strange Case of Mr. Lamb", p.Title);

        // A sidecar with only the episode number falls back to the name for the show, and to tvshow.nfo above the season folder.
        string sparse = MakeFile(@"Shows\Good Eats\Season 2\Good Eats - 2x05 - Fry Hard.mkv", "v");
        MakeFile(@"Shows\Good Eats\Season 2\Good Eats - 2x05 - Fry Hard.nfo", "<episodedetails><title>Fry Hard</title><aired>2000-01-12</aired></episodedetails>");
        MakeFile(@"Shows\Good Eats\tvshow.nfo", "<tvshow><title>Good Eats</title><genre>Food</genre></tvshow>");
        ParsedName q = Scanner.ParseFacts(sparse);
        Assert.Equal("Good Eats", q.Show);
        Assert.Equal(FactSource.Nfo, q.ShowSource);
        Assert.Equal(2, q.Season);
        Assert.Equal(5, q.Episode);
        Assert.Equal(new DateOnly(2000, 1, 12), q.AirDate);
        Assert.Contains("genre:food", q.Tags);

        // No sidecar at all: exactly the file-name result.
        string plain = MakeFile(@"Shows\TNG\Season 1\TNG S01E01 Encounter at Farpoint.mkv", "v");
        ParsedName fromName = FilenameParser.Parse(plain), fromFacts = Scanner.ParseFacts(plain);
        Assert.Equal(fromName with { Tags = [] }, fromFacts with { Tags = [] });
        Assert.Equal(fromName.Tags, fromFacts.Tags);
    }

    [Fact]
    public void A_movie_sidecar_turns_an_unknown_file_into_a_movie()
    {
        string video = MakeFile(@"Films\Spaceballs (1987)\Spaceballs.mkv", "v");
        MakeFile(@"Films\Spaceballs (1987)\Spaceballs.nfo", "<movie><title>Spaceballs</title><year>1987</year><genre>Comedy</genre></movie>");
        ParsedName p = Scanner.ParseFacts(video);
        Assert.Equal(MediaKind.Movie, p.Kind);
        Assert.Equal(FactSource.Nfo, p.KindSource);
        Assert.Equal("Spaceballs", p.Title);
        Assert.Equal(1987, p.Year);
        Assert.Contains("1980s", p.Tags);
        Assert.Contains("genre:comedy", p.Tags);
    }

    [Fact]
    public async Task Scan_uses_the_sidecar_and_a_rescan_picks_up_a_new_one_without_probing_again()
    {
        // A name the parser cannot place: no season or episode number. The sidecar arrives after the first scan.
        string video = MakeFile(@"Shows\Modern Marvels\Modern Marvels - Bridges.mkv", "v");
        var prober = new StubProber();
        var scanner = new Scanner(_db, prober);

        ScanReport first = await scanner.ScanAsync([_root]);
        Assert.Equal(1, first.Added);
        Assert.Equal(0, first.WithSidecar);
        Assert.Equal(0, _db.CountWithSidecar());
        Assert.Single(first.Unclassified);
        Assert.Equal(MediaKind.Unknown, _db.FindByPath(video)!.Kind);

        await Task.Delay(1100); // the sidecar must be newer than the scan, to the second
        MakeFile(@"Shows\Modern Marvels\Modern Marvels - Bridges.nfo",
            "<episodedetails><title>Bridges</title><showtitle>Modern Marvels</showtitle><season>4</season><episode>12</episode><aired>1998-03-09</aired></episodedetails>");

        ScanReport second = await scanner.ScanAsync([_root]);
        Assert.Equal(1, prober.Calls);
        Assert.Equal(1, second.Updated);
        Assert.Equal(1, second.WithSidecar);
        Assert.Equal(0, second.Unchanged);

        Assert.Equal(1, _db.CountWithSidecar());
        MediaItem item = _db.FindByPath(video)!;
        Assert.Equal(MediaKind.Episode, item.Kind);
        Assert.Equal(FactSource.Nfo, item.KindSource);
        Assert.Equal(1998, item.Year);
        EpisodeInfo ep = _db.GetEpisode(item.Id)!;
        Assert.Equal("Modern Marvels", ep.Show);
        Assert.Equal(4, ep.Season);
        Assert.Equal(12, ep.Episode);
        Assert.Equal("Bridges", ep.EpisodeTitle);
        Assert.Equal(FactSource.Nfo, ep.Source);
        Assert.Empty(_db.ListNeedingAttention(AttentionReason.EpisodeDetails));

        // Unchanged sidecar, unchanged video: nothing to do.
        ScanReport third = await scanner.ScanAsync([_root]);
        Assert.Equal(1, third.Unchanged);
        Assert.Equal(0, third.Updated);
    }

    [Fact]
    public async Task A_persons_answer_still_wins_over_the_sidecar()
    {
        string video = MakeFile(@"Shows\Mixed\Clip S01E01.mkv", "v");
        MakeFile(@"Shows\Mixed\Clip S01E01.nfo", "<episodedetails><showtitle>Wrong Show</showtitle><season>1</season><episode>1</episode></episodedetails>");
        var scanner = new Scanner(_db, new StubProber());
        await scanner.ScanAsync([_root]);
        long id = _db.FindByPath(video)!.Id;

        _db.SetKind(id, MediaKind.Commercial, FactSource.Manual);
        _db.UpsertEpisode(id, new EpisodeInfo { Show = "Right Show", Season = 2, Episode = 9, Source = FactSource.Manual });

        scanner.Retag();
        Assert.Equal(MediaKind.Commercial, _db.FindById(id)!.Kind);
        Assert.Equal("Right Show", _db.GetEpisode(id)!.Show);
    }
}
