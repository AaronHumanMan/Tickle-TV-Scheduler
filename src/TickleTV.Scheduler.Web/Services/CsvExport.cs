using System.Text;
using TickleTV.Scheduler.Core.Catalog;

namespace TickleTV.Scheduler.Web.Services;

/// <summary>Downloads of a search or a review list as a spreadsheet, every matching file, one row each.</summary>
public static class CsvExport
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/export/library.csv", (CatalogAccess catalog, string? text, string? kind, string? show, string? category, string? tag) =>
        {
            var filter = new CatalogDb.SearchFilter
            {
                Text = text,
                Kind = Enum.TryParse(kind, true, out MediaKind k) ? k : null,
                Show = show,
                Category = category,
                Tag = tag,
            };
            var rows = catalog.Run(db => db.Search(filter, int.MaxValue, 0));
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Build(rows, null))).ToArray(), "text/csv", "tickletv-library.csv");
        });

        app.MapGet("/export/review.csv", (CatalogAccess catalog, string? reason) =>
        {
            AttentionReason? only = Enum.TryParse(reason, true, out AttentionReason r) ? r : null;
            var rows = catalog.Run(db => db.ListNeedingAttention(only, int.MaxValue, 0));
            string csv = Build(rows.Select(x => x.Entry).ToList(), id => rows.First(x => x.Entry.Item.Id == id).Reason);
            string name = only is null ? "tickletv-needs-attention.csv" : $"tickletv-{LibraryActions.ReasonTitle(only.Value).ToLowerInvariant().Replace(' ', '-').Replace("'", "")}.csv";
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv", name);
        });
    }

    public static string Build(IReadOnlyList<CatalogEntry> entries, Func<long, AttentionReason>? reasonFor)
    {
        var sb = new StringBuilder();
        var header = new List<string> { "Kind", "Title", "Show", "Season", "Episode", "Episode title", "Length", "Chapter marks", "Year", "Brand", "Category", "Labels", "File" };
        if (reasonFor is not null) header.Insert(0, "Needs attention because");
        sb.AppendLine(string.Join(",", header.Select(Quote)));

        foreach (CatalogEntry e in entries)
        {
            var cells = new List<string>
            {
                LibraryActions.Describe(e.Item.Kind),
                e.Item.Title ?? Path.GetFileNameWithoutExtension(e.Item.Path),
                e.Episode?.Show ?? "",
                e.Episode?.Season?.ToString() ?? "",
                e.Episode?.Episode?.ToString() ?? "",
                e.Episode?.EpisodeTitle ?? "",
                TimeSpan.FromMilliseconds(e.Item.DurationMs).ToString(@"h\:mm\:ss"),
                e.Chapters.Count.ToString(),
                e.Item.Year?.ToString() ?? "",
                e.Tags.FirstOrDefault(t => t.Name.StartsWith("brand:"))?.Name["brand:".Length..] ?? "",
                e.Tags.FirstOrDefault(t => t.Name.StartsWith("category:"))?.Name["category:".Length..] ?? "",
                string.Join("; ", e.Tags.Where(t => !t.Name.Contains(':')).Select(t => t.Name)),
                e.Item.Path,
            };
            if (reasonFor is not null) cells.Insert(0, LibraryActions.ReasonTitle(reasonFor(e.Item.Id)));
            sb.AppendLine(string.Join(",", cells.Select(Quote)));
        }
        return sb.ToString();
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
