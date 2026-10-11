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
        ExtraLabels = string.Join(", ", e.Tags.Where(t => t.Source == FactSource.Manual && !t.Name.Contains(':') && t.Name != "brand-not-needed").Select(t => t.Name)),
    };

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
        _ => "",
    };

    public static string ReasonTitle(AttentionReason reason) => reason switch
    {
        AttentionReason.Unknown => "Don't know what it is",
        AttentionReason.EpisodeDetails => "Episode details missing",
        AttentionReason.NoBrand => "Commercials without a brand",
        AttentionReason.NoCategory => "Commercials without a category",
        _ => reason.ToString(),
    };
}
