using System.CommandLine;
using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Media;

namespace TickleTV.Scheduler.Cli.Commands;

public static class ScanCommand
{
    public static Command Create()
    {
        var roots = new Argument<DirectoryInfo[]>("roots") { Description = "Folders to scan. Any layout; files are classified by name and folder hints." };
        var db = CatalogCommand.DbOption();
        var ffprobe = new Option<string?>("--ffprobe") { Description = "Path to ffprobe (default: from PATH)." };
        var quiet = new Option<bool>("--quiet", "-q") { Description = "Only print the summary." };

        var cmd = new Command("scan", "Scan media folders into the catalog. Probes new and changed files only.");
        cmd.Arguments.Add(roots);
        cmd.Options.Add(db);
        cmd.Options.Add(ffprobe);
        cmd.Options.Add(quiet);

        cmd.SetAction(async (p, ct) =>
        {
            using var catalog = new CatalogDb(p.GetRequiredValue(db).FullName);
            var scanner = new Scanner(catalog, new Ffprobe(p.GetValue(ffprobe)), p.GetValue(quiet) ? null : Console.WriteLine);
            ScanReport r = await scanner.ScanAsync(p.GetRequiredValue(roots).Select(d => d.FullName).ToList(), ct);

            Console.WriteLine();
            Console.WriteLine($"Added {r.Added}, updated {r.Updated}, moved {r.Moved}, unchanged {r.Unchanged}, missing {r.Missing}.");
            if (r.Unclassified.Count > 0)
            {
                Console.WriteLine($"\n{r.Unclassified.Count} file(s) could not be classified. Set a kind with: catalog kind <id> <commercial|bumper|filler|movie|episode>");
                foreach (string f in r.Unclassified) Console.WriteLine($"  {f}");
            }
            foreach (string e in r.Errors) Console.WriteLine($"ERROR: {e}");
            return r.Errors.Count > 0 ? 1 : 0;
        });
        return cmd;
    }
}
