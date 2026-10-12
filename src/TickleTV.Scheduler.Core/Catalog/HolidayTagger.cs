using System.Text.RegularExpressions;
using TickleTV.Scheduler.Core.Config;

namespace TickleTV.Scheduler.Core.Catalog;

/// <summary>A holiday an episode or film belongs to, and how we know.</summary>
public sealed record HolidayHit(string Holiday, string Source)
{
    public string Tag => HolidayTagger.TagFor(Holiday);
}

/// <summary>
/// Spots holiday episodes: words in the title ("Treehouse of Horror", "Xmas"), words in the plot from a .nfo
/// sidecar, or an original air date in the ten days leading up to the holiday. Title and plot are solid evidence;
/// the air date is a hint, so it is recorded with its own source and only when nothing stronger matched.
/// A manual <c>not-holiday</c> label switches all of this off for a file.
/// </summary>
public static class HolidayTagger
{
    public const string Prefix = "holiday:";
    public const string NotHoliday = "not-holiday";

    public static string TagFor(string holiday) => Prefix + holiday;

    /// <summary>Christmas Eve episodes are Christmas episodes; the calendar keeps the two days apart, the tags do not.</summary>
    private static readonly IReadOnlyDictionary<string, string> Folded = new Dictionary<string, string> { ["christmas-eve"] = "christmas" };

    private static readonly IReadOnlyList<(string Holiday, Regex Words)> Keywords =
    [
        ("christmas", Rx(@"\bchristmas\b|\bx-?mas\b|\bsanta\b|\byule(?:tide)?\b|\bmistletoe\b|\bno[eë]l\b|\bkwanzaa\b|\bhanukkah\b|\bchanukah\b|\bfeliz navidad\b|\bgrinch\b|\bnutcracker\b|\bsilent night\b|\bjingle bells?\b|\breindeer\b|\bnorth pole\b|\bholiday special\b|\bstocking stuffer\b")),
        ("halloween", Rx(@"\bhallowe'?en\b|\btreehouse of horror\b|\btrick[\s-]or[\s-]treat(?:ers?)?\b|\ball hallows?'?\s*(?:eve|day)?\b|\bjack[\s-]?o'?[\s-]?lanterns?\b|\bhaunted\b|\bspooky\b")),
        ("thanksgiving", Rx(@"\bthanksgiving\b|\bturkey day\b")),
        ("new-years-day", Rx(@"\bnew year'?s?(?:\s+(?:eve|day))?\b|\bauld lang syne\b")),
        ("valentines-day", Rx(@"\bvalentine'?s?(?:\s+day)?\b|\bcupid\b")),
        ("st-patricks-day", Rx(@"\bs(?:ain)?t\.?\s*patrick'?s?(?:\s+day)?\b|\bleprechauns?\b|\bshamrocks?\b")),
        ("easter", Rx(@"\beaster\b")),
        ("independence-day", Rx(@"\b(?:fourth|4th) of july\b|\bindependence day\b")),
    ];

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Holidays named in a piece of text, in the order they are listed above.</summary>
    public static IReadOnlyList<string> InText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return Keywords.Where(k => k.Words.IsMatch(text)).Select(k => k.Holiday).ToList();
    }

    /// <summary>The holiday whose run-up (ten days before through the day itself) holds this date, if any.</summary>
    public static string? ByAirDate(DateOnly? airDate)
    {
        if (airDate is not { } date) return null;
        foreach (Holidays.Holiday h in Holidays.All)
        {
            DateOnly day = h.On(date.Year);
            if (date <= day && date >= day.AddDays(-10)) return Fold(h.Key);
        }
        return null;
    }

    /// <summary>
    /// Everything the evidence says. <paramref name="titleSource"/> is where the title came from (filename, nfo,
    /// manual); plot text always comes from a sidecar. The air date is used only when no words matched.
    /// </summary>
    public static IReadOnlyList<HolidayHit> Hits(string? title, string titleSource, string? plot, DateOnly? airDate)
    {
        var hits = new List<HolidayHit>();
        foreach (string h in InText(title)) hits.Add(new HolidayHit(Fold(h), titleSource));
        foreach (string h in InText(plot))
            if (!hits.Any(x => x.Holiday == Fold(h))) hits.Add(new HolidayHit(Fold(h), FactSource.Nfo));
        if (hits.Count == 0 && ByAirDate(airDate) is { } byDate) hits.Add(new HolidayHit(byDate, FactSource.AirDate));
        return hits;
    }

    /// <summary>The holidays a slot or a person can pick from: every calendar holiday except the folded ones.</summary>
    public static IReadOnlyList<Holidays.Holiday> Choices => Holidays.All.Where(h => !Folded.ContainsKey(h.Key)).ToList();

    public static string Fold(string holiday) => Folded.TryGetValue(holiday, out string? to) ? to : holiday;

    /// <summary>"Christmas Day" for christmas; the key itself when unknown.</summary>
    public static string Name(string holiday) => Holidays.Find(holiday)?.Name ?? holiday;
}
