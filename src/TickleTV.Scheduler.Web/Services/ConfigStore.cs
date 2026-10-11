using TickleTV.Scheduler.Core.Config;

namespace TickleTV.Scheduler.Web.Services;

/// <summary>Loads and saves the user's tickletv.yaml for the app. Creates a starter one on first use.</summary>
public sealed class ConfigStore
{
    private readonly object _gate = new();

    public string Path => ConfigLocations.Find() ?? ConfigLocations.DefaultConfigPath;
    public bool Exists => File.Exists(Path);

    public SchedulerConfig Load()
    {
        lock (_gate)
        {
            if (!Exists) return Starter();
            return SchedulerConfig.Load(Path);
        }
    }

    /// <summary>Loads without validation, so a config with no channels yet can still be edited.</summary>
    public SchedulerConfig LoadLoose()
    {
        lock (_gate)
        {
            if (!Exists) return Starter();
            try { return SchedulerConfig.Load(Path); }
            catch (InvalidDataException)
            {
                var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
                    .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties().Build();
                SchedulerConfig cfg = deserializer.Deserialize<SchedulerConfig>(File.ReadAllText(Path)) ?? new SchedulerConfig();
                cfg.BaseDirectory = System.IO.Path.GetDirectoryName(Path)!;
                return cfg;
            }
        }
    }

    public void Save(SchedulerConfig cfg)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            cfg.Save(Path);
        }
    }

    private static SchedulerConfig Starter() => new()
    {
        Catalog = ConfigLocations.CatalogFileName,
        BaseDirectory = ConfigLocations.DataFolder,
        Next = new NextConfig { Lineup = FindLineup() ?? @"C:\path\to\next\config\lineup.json" },
        Channels = [],
    };

    private static string? FindLineup()
    {
        string[] candidates =
        [
            System.IO.Path.Combine("C:\\ErsatzTV Next", "config", "lineup.json"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ersatztv", "config", "lineup.json"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
