using System.CommandLine;
using TickleTV.Scheduler.Core.Catalog;

namespace TickleTV.Scheduler.Cli.Commands;

/// <summary>Shows how the scanner would classify files, without probing or touching the catalog.</summary>
public static class ParseCommand
{
    private static readonly string[] Extensions = [".mkv", ".mp4", ".m4v", ".mov", ".avi", ".ts", ".webm", ".mpg", ".mpeg", ".wmv"];

    public static Command Create()
    {
        var roots = new Argument<string[]>("paths") { Description = "Files or folders to classify." };
        var summary = new Option<bool>("--summary") { Description = "Only print counts per kind." };

        var cmd = new Command("parse", "Dry-run the filename parser on files or folders. Probes nothing, writes nothing.");
        cmd.Arguments.Add(roots);
        cmd.Options.Add(summary);

        cmd.SetAction(p =>
        {
            var counts = new Dictionary<MediaKind, int>();
            foreach (string root in p.GetRequiredValue(roots))
            {
                IEnumerable<string> files = Directory.Exists(root)
                    ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    : [root];
                foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    ParsedName r = FilenameParser.Parse(file);
                    counts[r.Kind] = counts.GetValueOrDefault(r.Kind) + 1;
                    if (p.GetValue(summary)) continue;

                    string facts = r.Show is not null
                        ? $"show={r.Show} S{r.Season:00}E{r.Episode:00}{(r.AirDate is null ? "" : " " + r.AirDate)}"
                        : $"title={r.Title}";
                    string tags = r.Tags.Count == 0 ? "" : "  tags=" + string.Join(",", r.Tags);
                    Console.WriteLine($"{r.Kind,-10} {(r.Year?.ToString() ?? "----"),4}  {facts}{tags}");
                }
            }
            Console.WriteLine();
            foreach ((MediaKind kind, int n) in counts.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"{kind,-10} {n,5}");
            return 0;
        });
        return cmd;
    }
}
