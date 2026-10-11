using System.CommandLine;
using TickleTV.Scheduler.Core.Config;

namespace TickleTV.Scheduler.Cli.Commands;

/// <summary>
/// Where the config lives. Explicit path first, then <c>tickletv.yaml</c> in the current folder,
/// then the per-user data folder (<c>%LOCALAPPDATA%\TickleTV</c> on Windows, <c>~/.local/share/TickleTV</c> elsewhere).
/// </summary>
public static class ConfigLocator
{
    public const string FileName = "tickletv.yaml";

    public static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TickleTV");

    public static Option<FileInfo?> ConfigOption() => new("--config", "-c")
    {
        Description = $"Scheduler YAML config (default: ./{FileName}, then {Path.Combine(DataFolder, FileName)}).",
    };

    public static string Resolve(FileInfo? explicitPath)
    {
        if (explicitPath is not null) return explicitPath.FullName;
        string local = Path.GetFullPath(FileName);
        if (File.Exists(local)) return local;
        string user = Path.Combine(DataFolder, FileName);
        if (File.Exists(user)) return user;
        throw new FileNotFoundException($"No {FileName} found in the current folder or {DataFolder}. Run 'init' to create one, or pass --config.");
    }

    /// <summary>The catalog path the config names, or the data folder's default when there is no config.</summary>
    public static string DefaultCatalogPath()
    {
        try
        {
            SchedulerConfig cfg = SchedulerConfig.Load(Resolve(null));
            return cfg.ResolvePath(cfg.Catalog);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or YamlDotNet.Core.YamlException)
        {
            return Path.Combine(DataFolder, "tickletv.sqlite");
        }
    }
}
