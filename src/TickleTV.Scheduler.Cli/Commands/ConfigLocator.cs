using System.CommandLine;
using TickleTV.Scheduler.Core.Config;

namespace TickleTV.Scheduler.Cli.Commands;

/// <summary>CLI options over <see cref="ConfigLocations"/>.</summary>
public static class ConfigLocator
{
    public const string FileName = ConfigLocations.FileName;
    public static string DataFolder => ConfigLocations.DataFolder;

    public static Option<FileInfo?> ConfigOption() => new("--config", "-c")
    {
        Description = $"Scheduler YAML config (default: ./{FileName}, then {ConfigLocations.DefaultConfigPath}).",
    };

    public static string Resolve(FileInfo? explicitPath) => ConfigLocations.Resolve(explicitPath?.FullName);

    public static string DefaultCatalogPath() => ConfigLocations.DefaultCatalogPath();
}
