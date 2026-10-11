using System.CommandLine;
using System.Globalization;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Cli.Commands;

/// <summary>Builds playout windows for every channel in the config and writes them for Next. See <see cref="BuildRunner"/>.</summary>
public static class BuildCommand
{
    public static Command Create()
    {
        var config = ConfigLocator.ConfigOption();
        var from = new Option<string?>("--from") { Description = "Window start, ISO 8601 (default: next whole minute)." };
        var hours = new Option<int?>("--hours") { Description = "Override every channel's horizon." };
        var channel = new Option<string?>("--channel") { Description = "Build only this channel number." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the plan, write nothing, change no state." };
        var seed = new Option<int?>("--seed") { Description = "Random seed for shuffle order (reproducible builds)." };
        var fresh = new Option<bool>("--fresh") { Description = "Ignore the episodes the existing window already assigned and pick again from the rotation." };

        var cmd = new Command("build", "Build playout windows for all channels and write them for Next.");
        foreach (Option o in new Option[] { config, from, hours, channel, dryRun, seed, fresh }) cmd.Options.Add(o);

        cmd.SetAction(p =>
        {
            SchedulerConfig cfg = SchedulerConfig.Load(ConfigLocator.Resolve(p.GetValue(config)));
            var options = new BuildOptions
            {
                From = p.GetValue(from) is { } f ? DateTimeOffset.Parse(f, CultureInfo.InvariantCulture) : null,
                Hours = p.GetValue(hours),
                Channel = p.GetValue(channel),
                DryRun = p.GetValue(dryRun),
                Seed = p.GetValue(seed),
                Fresh = p.GetValue(fresh),
            };
            var reports = BuildRunner.Run(cfg, options, Console.WriteLine);
            return reports.Any(r => r.Error is not null) ? 2 : 0;
        });
        return cmd;
    }

    public static bool TryParseCompact(string s, out DateTimeOffset value) => BuildRunner.TryParseCompact(s, out value);
}
