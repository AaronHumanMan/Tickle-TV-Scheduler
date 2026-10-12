using TickleTV.Scheduler.Core.Catalog;

namespace TickleTV.Scheduler.Web.Services;

/// <summary>What a person can tell the catalog about a file. Everything here is a manual fact that survives rescans.</summary>
public sealed record LabelChoice
{
    public MediaKind Kind { get; set; } = MediaKind.Unknown;
    public string Show { get; set; } = "";
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string EpisodeTitle { get; set; } = "";
    public string Brand { get; set; } = "";
    public bool BrandNotNeeded { get; set; }
    public string Category { get; set; } = "";
    public string ExtraLabels { get; set; } = "";
    /// <summary>"" lets the scan decide; "none" means not a holiday episode; otherwise a holiday key such as christmas.</summary>
    public string Holiday { get; set; } = "";
}

public sealed class LibraryActions
{
    private readonly CatalogAccess _catalog;

    public LibraryActions(CatalogAccess catalog) => _catalog = catalog;

    public static LabelChoice ChoiceFor(CatalogEntry e) => new()
    {
        Kind = e.Item.Kind,
        Show = e.Episode?.Show ?? "",
        Season = e.Episode?.Season,
        Episode = e.Episode?.Episode,
        EpisodeTitle = e.Episode?.EpisodeTitle ?? "",
        Brand = e.Tags.FirstOrDefault(t => t.Name.StartsWith("brand:"))?.Name["brand:".Length..] ?? "",
        BrandNotNeeded = e.Tags.Any(t => t.Name == "brand-not-needed"),
        Category = e.Tags.FirstOrDefault(t => t.Name.StartsWith("category:"))?.Name["category:".Length..] ?? "",
        ExtraLabels = string.Join(", ", e.Tags.Where(t => t.Source == FactSource.Manual && !t.Name.Contains(':') && t.Name != "brand-not-needed" && t.Name != HolidayTagger.NotHoliday).Select(t => t.Name)),
        Holiday = e.Tags.Any(t => t.Source == FactSource.Manual && t.Name == HolidayTagger.NotHoliday) ? "none"
            : e.Tags.FirstOrDefault(t => t.Source == FactSource.Manual && t.Name.StartsWith(HolidayTagger.Prefix))?.Name[HolidayTagger.Prefix.Length..] ?? "",
    };

    /// <summary>The holiday tags the scan guessed for a file, as names: "Christmas Day, Halloween".</summary>
    public static string GuessedHolidays(CatalogEntry e) =>
        string.Join(", ", e.Tags.Where(t => t.Source != FactSource.Manual && t.Name.StartsWith(HolidayTagger.Prefix)).Select(t => HolidayTagger.Name(t.Name[HolidayTagger.Prefix.Length..])));

    /// <summary>Applies a person's choices. Returns a plain-language summary of what was saved.</summary>
    public string Apply(long itemId, LabelChoice c)
    {
        var saved = new List<string>();
        _catalog.Run(db =>
        {
            CatalogEntry? current = db.GetEntry(itemId);
            if (current is null) return;

            if (c.Kind != current.Item.Kind || current.Item.KindSource != FactSource.Manual)
            {
                db.SetKind(itemId, c.Kind, FactSource.Manual);
                foreach (MediaKind k in Enum.GetValues<MediaKind>()) db.RemoveTag(itemId, k.ToString());
                db.AddTag(itemId, c.Kind.ToString(), FactSource.Manual);
                saved.Add($"it is a {Describe(c.Kind)}");
            }

            if (c.Kind == MediaKind.Episode && !string.IsNullOrWhiteSpace(c.Show))
            {
                db.UpsertEpisode(itemId, new EpisodeInfo
                {
                    Show = c.Show.Trim(),
                    Season = c.Season,
                    Episode = c.Episode,
                    EpisodeTitle = string.IsNullOrWhiteSpace(c.EpisodeTitle) ? null : c.EpisodeTitle.Trim(),
                    AirDate = current.Episode?.AirDate,
                    Plot = current.Episode?.Plot,
                    Source = FactSource.Manual,
                });
                foreach (Tag t in current.Tags.Where(t => t.Name.StartsWith("show:"))) db.RemoveTag(itemId, t.Name);
                db.AddTag(itemId, "show:" + c.Show.Trim(), FactSource.Manual);
                saved.Add($"show {c.Show.Trim()}" + (c.Season is { } s && c.Episode is { } ep ? $" S{s:00}E{ep:00}" : ""));
            }

            if (c.Kind == MediaKind.Commercial)
            {
                foreach (Tag t in current.Tags.Where(t => t.Name.StartsWith("brand:") || t.Name == "brand-not-needed" || t.Name == "unbranded")) db.RemoveTag(itemId, t.Name);
                if (c.BrandNotNeeded)
                {
                    db.AddTag(itemId, "brand-not-needed", FactSource.Manual);
                    saved.Add("no brand needed");
                }
                else if (!string.IsNullOrWhiteSpace(c.Brand))
                {
                    db.AddTag(itemId, "brand:" + CatalogDb.NormalizeTag(c.Brand), FactSource.Manual);
                    saved.Add($"brand {c.Brand.Trim()}");
                }

                if (!string.IsNullOrWhiteSpace(c.Category))
                {
                    foreach (Tag t in current.Tags.Where(t => t.Name.StartsWith("category:"))) db.RemoveTag(itemId, t.Name);
                    db.AddCategory(c.Category);
                    db.AddTag(itemId, "category:" + CatalogDb.NormalizeTag(c.Category), FactSource.Manual);
                    saved.Add($"category {c.Category.Trim()}");
                }
            }

            if (c.Kind is MediaKind.Episode or MediaKind.Movie or MediaKind.Commercial or MediaKind.Bumper && !string.IsNullOrEmpty(c.Holiday))
            {
                foreach (Tag t in current.Tags.Where(t => t.Name.StartsWith(HolidayTagger.Prefix) || t.Name == HolidayTagger.NotHoliday)) db.RemoveTag(itemId, t.Name);
                if (c.Holiday == "none")
                {
                    db.AddTag(itemId, HolidayTagger.NotHoliday, FactSource.Manual);
                    saved.Add("not a holiday episode");
                }
                else
                {
                    db.AddTag(itemId, HolidayTagger.TagFor(c.Holiday), FactSource.Manual);
                    saved.Add($"{HolidayTagger.Name(c.Holiday)} episode");
                }
            }

            // A bumper has one role; picking one replaces the others.
            if (c.Kind == MediaKind.Bumper && c.ExtraLabels.Trim() is "promo" or "station-id" or "break-card")
                foreach (string role in new[] { "promo", "station-id", "break-card" }) db.RemoveTag(itemId, role);

            foreach (string label in c.ExtraLabels.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                db.AddTag(itemId, label, FactSource.Manual);
                saved.Add($"label {CatalogDb.NormalizeTag(label)}");
            }
        });
        return saved.Count == 0 ? "Nothing changed." : "Saved: " + string.Join(", ", saved) + ".";
    }

    public static string Describe(MediaKind kind) => kind switch
    {
        MediaKind.Episode => "TV episode",
        MediaKind.Movie => "movie",
        MediaKind.Commercial => "commercial",
        MediaKind.Bumper => "bumper (station ID or break card)",
        MediaKind.Filler => "filler (plays when nothing else fits)",
        MediaKind.Other => "not something to air (credits, titles, odds and ends)",
        _ => "unknown",
    };

    public static string ReasonText(AttentionReason reason) => reason switch
    {
        AttentionReason.Unknown => "I couldn't tell what this is from its name.",
        AttentionReason.EpisodeDetails => "This looks like a TV episode but I'm missing the show name, season, or episode number.",
        AttentionReason.NoBrand => "This commercial has no brand label yet.",
        AttentionReason.NoCategory => "This commercial has no category yet.",
        AttentionReason.BumperUnsorted => "This is in a bumper folder, but I can't tell if it's a station ID, a break card, or a promo for a show.",
        AttentionReason.NoChapters => "This episode has no chapter marks, so it will play straight through with commercials only at the end. Nothing to fix unless you want breaks inside it; adding chapter marks to the file does that.",
        AttentionReason.HolidayGuess => "The scan thinks this is a holiday episode, commercial or bumper. Press Yes or No so holiday slots and pools can trust it.",
        _ => "",
    };

    /// <summary>"The scan thinks this is a Christmas Day episode, from its title." Null when there is no unconfirmed guess.</summary>
    public static string? HolidayGuessText(CatalogEntry e)
    {
        var guesses = e.Tags.Where(t => t.Source != FactSource.Manual && t.Name.StartsWith(HolidayTagger.Prefix)).ToList();
        if (guesses.Count == 0 || e.Tags.Any(t => t.Source == FactSource.Manual && (t.Name == HolidayTagger.NotHoliday || t.Name.StartsWith(HolidayTagger.Prefix)))) return null;
        string names = string.Join(" and ", guesses.Select(t => HolidayTagger.Name(t.Name[HolidayTagger.Prefix.Length..])));
        string because = guesses[0].Source switch
        {
            FactSource.Nfo => "from its description",
            FactSource.AirDate => e.Episode?.AirDate is { } d ? $"only because it first aired on {d:MMMM d, yyyy}" : "from its air date",
            _ => "from its title",
        };
        string what = e.Item.Kind switch { MediaKind.Commercial => "commercial", MediaKind.Bumper => "bumper", MediaKind.Movie => "film", _ => "episode" };
        return $"The scan thinks this is a {names} {what}, {because}. Is that right?";
    }

    public static string ReasonTitle(AttentionReason reason) => reason switch
    {
        AttentionReason.Unknown => "Don't know what it is",
        AttentionReason.EpisodeDetails => "Episode details missing",
        AttentionReason.NoBrand => "Commercials without a brand",
        AttentionReason.NoCategory => "Commercials without a category",
        AttentionReason.BumperUnsorted => "Bumpers I couldn't sort",
        AttentionReason.NoChapters => "Episodes without chapter marks",
        AttentionReason.HolidayGuess => "Holiday episodes to confirm",
        _ => reason.ToString(),
    };

    /// <summary>
    /// Applies one set of answers to many files. Fields left blank keep each file's current value.
    /// For episodes, <paramref name="numberFrom"/> assigns episode numbers in list order starting there.
    /// </summary>
    public string ApplyBulk(IReadOnlyList<long> itemIds, LabelChoice c, int? numberFrom)
    {
        int done = 0;
        int next = numberFrom ?? 0;
        foreach (long id in itemIds)
        {
            CatalogEntry? current = _catalog.Run(db => db.GetEntry(id));
            if (current is null) continue;
            var per = new LabelChoice
            {
                Kind = c.Kind == MediaKind.Unknown ? current.Item.Kind : c.Kind,
                Show = string.IsNullOrWhiteSpace(c.Show) ? current.Episode?.Show ?? "" : c.Show,
                Season = c.Season ?? current.Episode?.Season,
                Episode = numberFrom is not null ? next++ : current.Episode?.Episode,
                EpisodeTitle = current.Episode?.EpisodeTitle ?? (current.Item.Title ?? ""),
                Brand = c.Brand,
                BrandNotNeeded = c.BrandNotNeeded,
                Category = c.Category,
                ExtraLabels = c.ExtraLabels,
                Holiday = c.Holiday,
            };
            Apply(id, per);
            done++;
        }
        return $"Saved {done} file{(done == 1 ? "" : "s")}.";
    }
}
