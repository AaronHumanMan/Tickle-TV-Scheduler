using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Media;

namespace TickleTV.Scheduler.Tests;

/// <summary>Holiday episodes: spotting them from titles, sidecar plots and air dates, and how people confirm or deny the guess.</summary>
public class HolidayTagTests : IDisposable
{
    private readonly string _root;
    private readonly CatalogDb _db;

    private sealed class StubProber : IMediaProber
    {
        public Task<MediaProbe> ProbeAsync(string path, CancellationToken ct = default) => Task.FromResult(new MediaProbe
        {
            Path = path, DurationMs = 1_320_000, Chapters = [],
            Streams = [new MediaStreamInfo(0, "video", "h264"), new MediaStreamInfo(1, "audio", "aac")],
        });
    }

    public HolidayTagTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tickletv-holiday-" + Guid.NewGuid().ToString("N"));
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
    public void Words_in_a_title_name_the_holiday()
    {
        Assert.Equal(["christmas"], HolidayTagger.InText("Simpsons Roasting on an Open Fire: A Christmas Special"));
        Assert.Equal(["christmas"], HolidayTagger.InText("Xmas Marks the Spot"));
        Assert.Equal(["halloween"], HolidayTagger.InText("Treehouse of Horror V"));
        Assert.Equal(["halloween"], HolidayTagger.InText("Trick-or-Treat"));
        Assert.Equal(["thanksgiving"], HolidayTagger.InText("Bart vs. Thanksgiving"));
        Assert.Equal(["new-years-day"], HolidayTagger.InText("The One with the New Year's Eve Party"));
        Assert.Equal(["valentines-day"], HolidayTagger.InText("I Love Lisa (a Valentine's story)"));
        Assert.Equal(["st-patricks-day"], HolidayTagger.InText("Homer vs. the Eighteenth Amendment: St. Patrick's Day"));
        Assert.Equal(["easter"], HolidayTagger.InText("The Easter Bunny Is Coming"));
        Assert.Equal(["independence-day"], HolidayTagger.InText("Summer of 4½ Ft: Fourth of July"));
        Assert.Empty(HolidayTagger.InText("Turkey, the country"));
        Assert.Empty(HolidayTagger.InText("Cape Feare"));
        Assert.Empty(HolidayTagger.InText(null));
    }

    [Fact]
    public void Air_dates_in_the_run_up_to_a_holiday_are_a_hint_and_christmas_eve_folds_into_christmas()
    {
        Assert.Equal("christmas", HolidayTagger.ByAirDate(new DateOnly(1989, 12, 17)));
        Assert.Equal("christmas", HolidayTagger.ByAirDate(new DateOnly(1991, 12, 24))); // Christmas Eve's own window folds into Christmas
        Assert.Equal("halloween", HolidayTagger.ByAirDate(new DateOnly(1990, 10, 25)));
        Assert.Equal("thanksgiving", HolidayTagger.ByAirDate(new DateOnly(1990, 11, 22))); // Thanksgiving 1990
        Assert.Null(HolidayTagger.ByAirDate(new DateOnly(1990, 11, 1)));
        Assert.Null(HolidayTagger.ByAirDate(new DateOnly(1990, 12, 1)));
        Assert.Null(HolidayTagger.ByAirDate(null));

        // Words beat the date; the date is used only when no words matched.
        var byWords = HolidayTagger.Hits("Treehouse of Horror", FactSource.Filename, null, new DateOnly(1990, 12, 20));
        Assert.Equal([new HolidayHit("halloween", FactSource.Filename)], byWords);
        var byPlot = HolidayTagger.Hits("Marge Be Not Proud", FactSource.Filename, "Bart shoplifts a video game as a Christmas present.", null);
        Assert.Equal([new HolidayHit("christmas", FactSource.Nfo)], byPlot);
        var byDate = HolidayTagger.Hits("Homer's Night Out", FactSource.Filename, null, new DateOnly(1990, 12, 20));
        Assert.Equal([new HolidayHit("christmas", FactSource.AirDate)], byDate);
        Assert.Equal("holiday:christmas", byDate[0].Tag);
    }

    [Fact]
    public async Task The_scan_tags_holiday_episodes_and_the_review_queue_asks_for_confirmation()
    {
        string xmas = MakeFile(@"Shows\The Simpsons\Season 1\The Simpsons - 1989-12-17 - S01E01 - Simpsons Roasting on an Open Fire (Christmas Special).mkv", "video-1");
        string plot = MakeFile(@"Shows\The Simpsons\Season 7\The Simpsons - S07E11 - Marge Be Not Proud.mkv", "video-2");
        MakeFile(@"Shows\The Simpsons\Season 7\The Simpsons - S07E11 - Marge Be Not Proud.nfo",
            "<episodedetails><title>Marge Be Not Proud</title><showtitle>The Simpsons</showtitle><season>7</season><episode>11</episode><aired>1995-12-17</aired><plot>Bart shoplifts a video game he wants for Christmas.</plot></episodedetails>");
        string hint = MakeFile(@"Shows\The Simpsons\Season 2\The Simpsons - 1990-12-20 - S02E10 - Bart Gets Hit by a Car.mkv", "video-3");
        string plain = MakeFile(@"Shows\The Simpsons\Season 5\The Simpsons - S05E02 - Cape Feare.mkv", "video-4");

        ScanReport report = await new Scanner(_db, new StubProber()).ScanAsync([_root]);
        Assert.Empty(report.Errors);
        Assert.Equal(4, report.Added);

        CatalogEntry a = _db.GetEntry(_db.FindByPath(xmas)!.Id)!;
        Assert.Contains(a.Tags, t => t.Name == "holiday:christmas" && t.Source == FactSource.Filename);
        CatalogEntry b = _db.GetEntry(_db.FindByPath(plot)!.Id)!;
        Assert.Contains(b.Tags, t => t.Name == "holiday:christmas" && t.Source == FactSource.Nfo);
        Assert.Equal("Bart shoplifts a video game he wants for Christmas.", b.Episode!.Plot);
        CatalogEntry c = _db.GetEntry(_db.FindByPath(hint)!.Id)!;
        Assert.Contains(c.Tags, t => t.Name == "holiday:christmas" && t.Source == FactSource.AirDate);
        CatalogEntry d = _db.GetEntry(_db.FindByPath(plain)!.Id)!;
        Assert.DoesNotContain(d.Tags, t => t.Name.StartsWith("holiday:"));

        var toConfirm = _db.ListNeedingAttention(AttentionReason.HolidayGuess);
        Assert.Equal(3, toConfirm.Count);
        Assert.Equal(3, _db.CountNeedingAttention()[AttentionReason.HolidayGuess]);
        // Confirmations are their own list, not part of "All".
        Assert.DoesNotContain(_db.ListNeedingAttention(), r => r.Reason == AttentionReason.HolidayGuess);

        ShowSummary summary = _db.ListShowSummaries().Single();
        Assert.Equal(3, summary.Holidays["christmas"]);
        Assert.Equal(3, _db.HolidayEpisodesOf("The Simpsons", "christmas").Count);
        Assert.Equal(new DateOnly(1989, 12, 17), _db.HolidayEpisodesOf("The Simpsons", "christmas")[0].Episode!.AirDate);
    }

    [Fact]
    public async Task Commercials_and_bumpers_get_holiday_tags_too_and_pools_can_ask_for_them()
    {
        string ad = MakeFile(@"Commercials\90s\Food\Coca-Cola - Christmas polar bears 1993.mp4", "ad-1");
        string plain = MakeFile(@"Commercials\90s\Food\Taco Bell MC Hammer Commercial 1991.mp4", "ad-2");
        string bumper = MakeFile(@"Commercials\90s\Bumpers and Promos\ABC - Happy Halloween, we'll be right back.mp4", "bump-1");
        ScanReport report = await new Scanner(_db, new StubProber()).ScanAsync([_root]);
        Assert.Empty(report.Errors);

        CatalogEntry a = _db.GetEntry(_db.FindByPath(ad)!.Id)!;
        Assert.Equal(MediaKind.Commercial, a.Item.Kind);
        Assert.Contains(a.Tags, t => t.Name == "holiday:christmas" && t.Source == FactSource.Filename);
        CatalogEntry b = _db.GetEntry(_db.FindByPath(bumper)!.Id)!;
        Assert.Equal(MediaKind.Bumper, b.Item.Kind);
        Assert.Contains(b.Tags, t => t.Name == "holiday:halloween");
        Assert.DoesNotContain(_db.GetEntry(_db.FindByPath(plain)!.Id)!.Tags, t => t.Name.StartsWith("holiday:"));

        Assert.Equal(2, _db.CountNeedingAttention()[AttentionReason.HolidayGuess]);
        var pool = PoolQuery.Parse("kind = commercial AND tag:holiday:christmas");
        var picked = pool.Filter(_db.ListItems(MediaKind.Commercial).Select(i => _db.GetEntry(i.Id)!)).ToList();
        Assert.Single(picked);
        Assert.Equal(ad, picked[0].Item.Path);
    }

    [Fact]
    public async Task A_persons_answer_sticks_through_a_rescan()
    {
        string hint = MakeFile(@"Shows\The Simpsons\Season 2\The Simpsons - 1990-12-20 - S02E10 - Bart Gets Hit by a Car.mkv", "video-5");
        string plain = MakeFile(@"Shows\The Simpsons\Season 5\The Simpsons - S05E02 - Cape Feare.mkv", "video-6");
        var scanner = new Scanner(_db, new StubProber());
        ScanReport report = await scanner.ScanAsync([_root]);
        Assert.Empty(report.Errors);
        long hintId = _db.FindByPath(hint)!.Id, plainId = _db.FindByPath(plain)!.Id;

        // "No, not a holiday episode" on the date hint; "Yes, Halloween" on the plain one (a person knows better).
        _db.RemoveTag(hintId, "holiday:christmas");
        _db.AddTag(hintId, HolidayTagger.NotHoliday, FactSource.Manual);
        _db.AddTag(plainId, "holiday:halloween", FactSource.Manual);

        scanner.Retag();

        CatalogEntry h = _db.GetEntry(hintId)!;
        Assert.DoesNotContain(h.Tags, t => t.Name.StartsWith("holiday:"));
        Assert.Contains(h.Tags, t => t.Name == HolidayTagger.NotHoliday && t.Source == FactSource.Manual);
        CatalogEntry p = _db.GetEntry(plainId)!;
        Assert.Contains(p.Tags, t => t.Name == "holiday:halloween" && t.Source == FactSource.Manual);
        Assert.Empty(_db.ListNeedingAttention(AttentionReason.HolidayGuess));
        Assert.Equal(["halloween"], _db.ListShowSummaries().Single().Holidays.Keys);
        Assert.Empty(_db.HolidayEpisodesOf("The Simpsons", "christmas"));
    }
}
