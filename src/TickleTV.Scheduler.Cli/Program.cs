using System.CommandLine;
using TickleTV.Scheduler.Cli.Commands;

var root = new RootCommand("Tickle TV Scheduler: builds broadcast-style playouts for ErsatzTV Next.");
root.Subcommands.Add(ProbeCommand.Create());
root.Subcommands.Add(SpikeCommand.Create());
root.Subcommands.Add(ScanCommand.Create());
root.Subcommands.Add(CatalogCommand.Create());

return await root.Parse(args).InvokeAsync();
