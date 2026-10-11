using System.CommandLine;
using TickleTV.Scheduler.Core.Catalog;

namespace TickleTV.Scheduler.Cli.Commands;

public static class CatalogCommand
{
    public static Option<FileInfo> DbOption() => new("--db")
    {
        Description = "Catalog database file.",
        DefaultValueFactory = _ => new FileInfo(Path.Combine(AppContext.BaseDirectory, "tickletv.sqlite")),
    };

    public static Command Create()
    {
        var cmd = new Command("catalog", "Inspect and correct the media catalog.");
        cmd.Subcommands.Add(List());
        cmd.Subcommands.Add(Shows());
        cmd.Subcommands.Add(Show());
        cmd.Subcommands.Add(Kind());
        cmd.Subcommands.Add(TagCmd());
        cmd.Subcommands.Add(Untag());
        return cmd;
    }

    private static Command List()
    {
        var db = DbOption();
        var kind = new Option<MediaKind?>("--kind") { Description = "Only this kind." };
        var tag = new Option<string?>("--tag") { Description = "Only items with this tag." };
        var cmd = new Command("list", "List catalog items.");
        cmd.Options.Add(db);
        cmd.Options.Add(kind);
        cmd.Options.Add(tag);
        cmd.SetAction(p =>
        {
            using var catalog = new CatalogDb(p.GetRequiredValue(db).FullName);
            string? wanted = p.GetValue(tag) is { } t ? CatalogDb.NormalizeTag(t) : null;
            Console.WriteLine($"{"id",5}  {"kind",-10} {"length",9}  {"year",4}  title / tags");
            foreach (MediaItem item in catalog.ListItems(p.GetValue(kind)))
            {
                var tags = catalog.GetTags(item.Id);
                if (wanted is not null && tags.All(x => x.Name != wanted)) continue;
                Console.WriteLine($"{item.Id,5}  {item.Kind,-10} {ProbeCommand.Fmt(item.DurationMs),9}  {item.Year?.ToString() ?? "    ",4}  {item.Title}");
                Console.WriteLine($"{"",5}  {"",-10} {"",9}  {"",4}  {string.Join(", ", tags.Select(x => x.Source == FactSource.Manual ? x.Name + "*" : x.Name))}");
            }
            return 0;
        });
        return cmd;
    }

    private static Command Shows()
    {
        var db = DbOption();
        var cmd = new Command("shows", "List shows and how many episodes each has.");
        cmd.Options.Add(db);
        cmd.SetAction(p =>
        {
            using var catalog = new CatalogDb(p.GetRequiredValue(db).FullName);
            foreach (string show in catalog.ListShows())
            {
                var eps = catalog.EpisodesOf(show);
                int withChapters = eps.Count(e => e.Chapters.Count > 1);
                int withDates = eps.Count(e => e.Episode?.AirDate is not null);
                Console.WriteLine($"{show}: {eps.Count} episodes, {withChapters} with chapters, {withDates} with air dates");
            }
            return 0;
        });
        return cmd;
    }

    private static Command Show()
    {
        var db = DbOption();
        var name = new Argument<string>("show") { Description = "Show name as listed by 'catalog shows'." };
        var cmd = new Command("show", "List a show's episodes in broadcast order.");
        cmd.Options.Add(db);
        cmd.Arguments.Add(name);
        cmd.SetAction(p =>
        {
            using var catalog = new CatalogDb(p.GetRequiredValue(db).FullName);
            foreach (CatalogEntry e in catalog.EpisodesOf(p.GetRequiredValue(name)))
            {
                EpisodeInfo ep = e.Episode!;
                Console.WriteLine($"{e.Item.Id,5}  S{ep.Season:00}E{ep.Episode:00}  {ep.AirDate?.ToString("yyyy-MM-dd") ?? "          "}  {ProbeCommand.Fmt(e.Item.DurationMs),9}  {e.Chapters.Count,2} ch  {ep.EpisodeTitle}");
            }
            return 0;
        });
        return cmd;
    }

    private static Command Kind()
    {
        var db = DbOption();
        var id = new Argument<long>("id");
        var kind = new Argument<MediaKind>("kind");
        var cmd = new Command("kind", "Set an item's kind by hand. Rescans will not change it.");
        cmd.Options.Add(db);
        cmd.Arguments.Add(id);
        cmd.Arguments.Add(kind);
        cmd.SetAction(p =>
        {
            using var catalog = new CatalogDb(p.GetRequiredValue(db).FullName);
            long itemId = p.GetRequiredValue(id);
            if (catalog.FindById(itemId) is null) { Console.Error.WriteLine($"No item {itemId}."); return 2; }
            catalog.SetKind(itemId, p.GetRequiredValue(kind), FactSource.Manual);
            catalog.RemoveTag(itemId, "unknown");
            catalog.AddTag(itemId, p.GetRequiredValue(kind).ToString(), FactSource.Manual);
            Console.WriteLine($"Item {itemId} is now {p.GetRequiredValue(kind)}.");
            return 0;
        });
        return cmd;
    }

    private static Command TagCmd()
    {
        var db = DbOption();
        var id = new Argument<long>("id");
        var tags = new Argument<string[]>("tags") { Description = "One or more tags, such as kids or taco-bell." };
        var cmd = new Command("tag", "Add manual tags to an item. Manual tags survive rescans.");
        cmd.Options.Add(db);
        cmd.Arguments.Add(id);
        cmd.Arguments.Add(tags);
        cmd.SetAction(p =>
        {
            using var catalog = new CatalogDb(p.GetRequiredValue(db).FullName);
            long itemId = p.GetRequiredValue(id);
            if (catalog.FindById(itemId) is null) { Console.Error.WriteLine($"No item {itemId}."); return 2; }
            foreach (string t in p.GetRequiredValue(tags)) catalog.AddTag(itemId, t, FactSource.Manual);
            Console.WriteLine($"Item {itemId} tags: {string.Join(", ", catalog.GetTags(itemId).Select(x => x.Name))}");
            return 0;
        });
        return cmd;
    }

    private static Command Untag()
    {
        var db = DbOption();
        var id = new Argument<long>("id");
        var tags = new Argument<string[]>("tags");
        var cmd = new Command("untag", "Remove tags from an item.");
        cmd.Options.Add(db);
        cmd.Arguments.Add(id);
        cmd.Arguments.Add(tags);
        cmd.SetAction(p =>
        {
            using var catalog = new CatalogDb(p.GetRequiredValue(db).FullName);
            long itemId = p.GetRequiredValue(id);
            foreach (string t in p.GetRequiredValue(tags)) catalog.RemoveTag(itemId, t);
            Console.WriteLine($"Item {itemId} tags: {string.Join(", ", catalog.GetTags(itemId).Select(x => x.Name))}");
            return 0;
        });
        return cmd;
    }
}
