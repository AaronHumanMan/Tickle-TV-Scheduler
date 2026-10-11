namespace TickleTV.Scheduler.Core.Config;

/// <summary>
/// Where the config and catalog live: an explicit path, then <c>tickletv.yaml</c> in the current folder,
/// then the per-user data folder (<c>%LOCALAPPDATA%\TickleTV</c> on Windows, <c>~/.local/share/TickleTV</c> elsewhere).
/// </summary>
public static class ConfigLocations
{
    public const string FileName = "tickletv.yaml";
    public const string CatalogFileName = "tickletv.sqlite";

    public static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TickleTV");

    public static string DefaultConfigPath => Path.Combine(DataFolder, FileName);

    /// <summary>The config path to use, or null when none exists yet.</summary>
    public static string? Find(string? explicitPath = null)
    {
        if (explicitPath is not null) return Path.GetFullPath(explicitPath);
        string local = Path.GetFullPath(FileName);
        if (File.Exists(local)) return local;
        string user = DefaultConfigPath;
        return File.Exists(user) ? user : null;
    }

    public static string Resolve(string? explicitPath = null) =>
        Find(explicitPath) ?? throw new FileNotFoundException($"No {FileName} found in the current folder or {DataFolder}. Run 'init' to create one, or pass --config.");

    /// <summary>The catalog path the config names, or the data folder's default when there is no usable config.</summary>
    public static string DefaultCatalogPath(string? explicitConfig = null)
    {
        try
        {
            string? path = Find(explicitConfig);
            if (path is null) return Path.Combine(DataFolder, CatalogFileName);
            SchedulerConfig cfg = SchedulerConfig.Load(path);
            return cfg.ResolvePath(cfg.Catalog);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or YamlDotNet.Core.YamlException)
        {
            return Path.Combine(DataFolder, CatalogFileName);
        }
    }
}
