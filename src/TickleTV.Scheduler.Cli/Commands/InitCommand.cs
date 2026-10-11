using System.CommandLine;
using System.Text.Json;

namespace TickleTV.Scheduler.Cli.Commands;

/// <summary>Creates a starter config in the data folder (or a folder you name) and points it at Next.</summary>
public static class InitCommand
{
    public static Command Create()
    {
        var folder = new Argument<DirectoryInfo?>("folder") { Description = $"Where to put {ConfigLocator.FileName} and the catalog (default: {ConfigLocator.DataFolder}).", Arity = ArgumentArity.ZeroOrOne };
        var lineup = new Option<FileInfo?>("--lineup") { Description = "Next's lineup.json (default: found automatically when possible)." };
        var force = new Option<bool>("--force") { Description = "Overwrite an existing config." };

        var cmd = new Command("init", "Create a starter tickletv.yaml and catalog location.");
        cmd.Arguments.Add(folder);
        cmd.Options.Add(lineup);
        cmd.Options.Add(force);

        cmd.SetAction(p =>
        {
            string dir = p.GetValue(folder)?.FullName ?? ConfigLocator.DataFolder;
            Directory.CreateDirectory(dir);
            string configPath = Path.Combine(dir, ConfigLocator.FileName);
            if (File.Exists(configPath) && !p.GetValue(force))
            {
                Console.Error.WriteLine($"{configPath} already exists. Use --force to overwrite.");
                return 2;
            }

            string? lineupPath = p.GetValue(lineup)?.FullName ?? FindLineup();
            string lineupLine = lineupPath ?? @"C:\path\to\next\config\lineup.json";
            (string number, string name, string tvg) = lineupPath is null ? ("1", "Tickle TV", "tickle.1") : FirstChannel(lineupPath);

            File.WriteAllText(configPath, Template(lineupLine, number, name, tvg));
            Console.WriteLine($"Wrote {configPath}");
            Console.WriteLine(lineupPath is null
                ? "Next's lineup.json was not found; edit next.lineup in the config."
                : $"Next lineup: {lineupPath}");
            Console.WriteLine();
            Console.WriteLine("Next steps:");
            Console.WriteLine($"  scan \"D:\\Your\\Media\" --db \"{Path.Combine(dir, "tickletv.sqlite")}\"");
            Console.WriteLine($"  catalog shows");
            Console.WriteLine($"  build --dry-run");
            return 0;
        });
        return cmd;
    }

    private static string? FindLineup()
    {
        string[] candidates =
        [
            Path.Combine("C:\\ErsatzTV Next", "config", "lineup.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ersatztv", "config", "lineup.json"),
            Path.Combine("config", "lineup.json"),
            "lineup.json",
        ];
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static (string Number, string Name, string TvgId) FirstChannel(string lineupPath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(lineupPath));
            JsonElement c = doc.RootElement.GetProperty("channels")[0];
            string number = c.GetProperty("number").GetString() ?? "1";
            string name = c.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "Tickle TV" : "Tickle TV";
            string tvg = c.TryGetProperty("tvg_id", out JsonElement t) ? t.GetString() ?? $"tickle.{number}" : $"tickle.{number}";
            return (number, name, tvg);
        }
        catch
        {
            return ("1", "Tickle TV", "tickle.1");
        }
    }

    private static string Template(string lineup, string number, string name, string tvg) => $"""
        # Tickle TV Scheduler config. Paths are relative to this file.

        catalog: tickletv.sqlite

        next:
          lineup: {lineup}
          schema_version: 0.0.5   # 0.0.5 for Next 0.2.0; 0.0.4 for legacy ErsatzTV's embedded engine

        channels:
          - number: "{number}"
            name: {name}
            tvg_id: {tvg}
            horizon_hours: 24

            defaults:
              pad: 30              # 30, 60, 5, 10, or none
              max_breaks: 3
              min_act_seconds: 120
              pre_roll: false
              post_roll: true
              cold_open_break: true

            ads:
              pool: kind = commercial
              cooldown_minutes: 90
              max_repeats_per_break: 1

            bumpers:
              pool: kind = bumper
              before_break: true
              after_break: true
              min_ad_seconds: 15

            filler:
              pool: kind = filler

            # Daily grid: each slot airs its show back to back until the next slot starts.
            # Show names come from 'catalog shows'. order: chronological | shuffle
            schedule:
              - time: "06:00"
                show: The Simpsons
                order: shuffle
              - time: "20:00"
                show: TNG
                order: chronological
                pad: 60
                max_breaks: 4

        """;
}
