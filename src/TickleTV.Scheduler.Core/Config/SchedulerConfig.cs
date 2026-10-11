using TickleTV.Scheduler.Core.Engine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TickleTV.Scheduler.Core.Config;

/// <summary>Top-level YAML config. Paths are resolved relative to the config file's folder.</summary>
public sealed class SchedulerConfig
{
    public string Catalog { get; set; } = "tickletv.sqlite";
    /// <summary>Folders the app scans. The CLI can scan any folder; the app scans these.</summary>
    public List<string> MediaRoots { get; set; } = [];
    public NextConfig Next { get; set; } = new();
    public List<ChannelConfig> Channels { get; set; } = [];

    /// <summary>Folder the config was loaded from; set by <see cref="Load"/>.</summary>
    [YamlIgnore]
    public string BaseDirectory { get; set; } = Environment.CurrentDirectory;

    public string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(BaseDirectory, path));

    public static SchedulerConfig Load(string path)
    {
        string full = Path.GetFullPath(path);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        SchedulerConfig cfg = deserializer.Deserialize<SchedulerConfig>(File.ReadAllText(full)) ?? new SchedulerConfig();
        cfg.BaseDirectory = Path.GetDirectoryName(full) ?? Environment.CurrentDirectory;
        cfg.Validate();
        return cfg;
    }

    public static SchedulerConfig Parse(string yaml, string baseDirectory)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        SchedulerConfig cfg = deserializer.Deserialize<SchedulerConfig>(yaml) ?? new SchedulerConfig();
        cfg.BaseDirectory = baseDirectory;
        cfg.Validate();
        return cfg;
    }

    /// <summary>Writes the config back to disk, preserving a comment header.</summary>
    public void Save(string path)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
            .Build();
        string yaml = "# Tickle TV Scheduler config. Edited by the app; paths are relative to this file.\n\n" + serializer.Serialize(this);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, yaml);
        File.Move(tmp, path, overwrite: true);
    }

    public void Validate()
    {
        if (Channels.Count == 0) throw new InvalidDataException("Config has no channels.");
        foreach (ChannelConfig ch in Channels)
        {
            if (string.IsNullOrWhiteSpace(ch.Number)) throw new InvalidDataException("Every channel needs a number.");
            if (string.IsNullOrWhiteSpace(ch.TvgId)) ch.TvgId = $"tickle.{ch.Number}";
            if (ch.Schedule.Count == 0) throw new InvalidDataException($"Channel {ch.Number} has no schedule slots.");
            foreach (SlotConfig slot in ch.Schedule)
            {
                if (string.IsNullOrWhiteSpace(slot.Show)) throw new InvalidDataException($"Channel {ch.Number}: every slot needs a show.");
                _ = slot.StartTime; // throws on a bad time
                _ = slot.OrderMode;
            }
            _ = ch.Defaults.PaddingMode;
        }
    }
}

public sealed class NextConfig
{
    /// <summary>Path to Next's lineup.json. Channel playout folders and the xmltv folder are read from it.</summary>
    public string Lineup { get; set; } = "lineup.json";
    public string SchemaVersion { get; set; } = Playout.Playout.DefaultSchemaVersion;
}

public sealed class ChannelConfig
{
    public string Number { get; set; } = "1";
    public string TvgId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>How far ahead a build writes, in hours.</summary>
    public int HorizonHours { get; set; } = 24;
    public SlotDefaults Defaults { get; set; } = new();
    public AdsConfig Ads { get; set; } = new();
    public BumpersConfig Bumpers { get; set; } = new();
    public FillerConfig Filler { get; set; } = new();
    public GapConfig Gap { get; set; } = new();
    public List<SlotConfig> Schedule { get; set; } = [];
}

/// <summary>
/// Holding the grid: when the next episode cannot finish before the following slot starts, the time until that
/// slot is filled from this pool so the slot starts on time. Longer gaps air the episode anyway and drift.
/// </summary>
public sealed class GapConfig
{
    public bool HoldGrid { get; set; } = true;
    /// <summary>Clips that fill a station break. Default: network promos and commercials.</summary>
    public string Pool { get; set; } = "kind = bumper AND tag:promo OR kind = commercial";
    /// <summary>The longest gap worth filling instead of airing one more episode.</summary>
    public int MaxMinutes { get; set; } = 12;
}

public sealed class FillerConfig
{
    /// <summary>Pool query for the clip that covers gaps ads cannot fill. The first match is used; it loops as needed. Empty means black slate.</summary>
    public string Pool { get; set; } = "kind = filler";
}

public sealed class BumpersConfig
{
    /// <summary>Pool query for bumper candidates. Empty or no matches means no bumpers.</summary>
    public string Pool { get; set; } = "kind = bumper";
    /// <summary>Play a bumper right before the ads in each break ("we'll be right back").</summary>
    public bool BeforeBreak { get; set; } = true;
    /// <summary>Play a bumper right after the ads, before the show resumes ("now back to...").</summary>
    public bool AfterBreak { get; set; } = true;
    /// <summary>Optional tag that narrows the before-break pick, such as break-out.</summary>
    public string? BeforeTag { get; set; }
    /// <summary>Optional tag that narrows the after-break pick, such as break-in.</summary>
    public string? AfterTag { get; set; }
    /// <summary>Leave at least this many seconds of ads in a break; bumpers are skipped when they would crowd ads out.</summary>
    public int MinAdSeconds { get; set; } = 15;
    /// <summary>show_edges (default): one bumper before the show and one after it. every_break: bumpers around the ads in every break.</summary>
    public string Placement { get; set; } = "show_edges";

    [YamlIgnore]
    public BumperPlacement PlacementMode => Placement.Trim().ToLowerInvariant() switch
    {
        "show_edges" or "edges" or "show" => BumperPlacement.ShowEdges,
        "every_break" or "breaks" or "all" => BumperPlacement.EveryBreak,
        _ => throw new InvalidDataException($"Unknown bumper placement '{Placement}'. Use show_edges or every_break."),
    };
}

public sealed class SlotDefaults
{
    /// <summary>30, 60, 5, 10, or none.</summary>
    public string Pad { get; set; } = "30";
    public int MaxBreaks { get; set; } = 3;
    public int MinActSeconds { get; set; } = 120;
    public bool PreRoll { get; set; } = false;
    public bool PostRoll { get; set; } = true;
    /// <summary>Mid-show breaks for episodes with no usable chapter marks. 0 means none: the show runs straight through.</summary>
    public int FallbackBreaks { get; set; } = 0;
    /// <summary>Padding used for an episode that gets no mid-show breaks: 30, 60, 5, 10, or none. Default: pad to the next 5 minutes.</summary>
    public string NoChapterPad { get; set; } = "5";
    /// <summary>Always break after a short first chapter (cold open or teaser), the classic TV pattern.</summary>
    public bool ColdOpenBreak { get; set; } = true;

    [YamlIgnore]
    public PaddingMode PaddingMode => ParsePad(Pad);

    [YamlIgnore]
    public PaddingMode NoChapterPaddingMode => ParsePad(NoChapterPad);

    public static PaddingMode ParsePad(string value) => value.Trim().ToLowerInvariant() switch
    {
        "30" => PaddingMode.HalfHour,
        "60" => PaddingMode.Hour,
        "5" => PaddingMode.Next5,
        "10" => PaddingMode.Next10,
        "none" or "0" => PaddingMode.None,
        _ => throw new InvalidDataException($"Unknown pad '{value}'. Use 30, 60, 5, 10, or none."),
    };
}

public sealed class AdsConfig
{
    /// <summary>Pool query, see <see cref="Catalog.PoolQuery"/>. Default: every commercial.</summary>
    public string Pool { get; set; } = "kind = commercial";
    /// <summary>Do not air the same clip again within this many minutes.</summary>
    public int CooldownMinutes { get; set; } = 90;
    public int MaxRepeatsPerBreak { get; set; } = 1;
}

public sealed class SlotConfig
{
    /// <summary>Daily start time, HH:mm. The slot runs until the next slot's start.</summary>
    public string Time { get; set; } = "00:00";
    public string Show { get; set; } = "";
    /// <summary>chronological or shuffle.</summary>
    public string Order { get; set; } = "chronological";
    public string? Pad { get; set; }
    public int? MaxBreaks { get; set; }
    public int? MinActSeconds { get; set; }
    public bool? PreRoll { get; set; }
    public bool? PostRoll { get; set; }

    [YamlIgnore]
    public TimeOnly StartTime => TimeOnly.TryParseExact(Time.Trim(), ["H:mm", "HH:mm", "H:mm:ss"], out TimeOnly t)
        ? t
        : throw new InvalidDataException($"Bad slot time '{Time}'. Use HH:mm.");

    [YamlIgnore]
    public OrderMode OrderMode => Order.Trim().ToLowerInvariant() switch
    {
        "chronological" or "chrono" or "sequential" => OrderMode.Chronological,
        "shuffle" or "random" => OrderMode.Shuffle,
        _ => throw new InvalidDataException($"Unknown order '{Order}'. Use chronological or shuffle."),
    };

    public BreakPolicy BreakPolicy(SlotDefaults d) => new()
    {
        MaxMidBreaks = MaxBreaks ?? d.MaxBreaks,
        MinActMs = (MinActSeconds ?? d.MinActSeconds) * 1000L,
        PreRoll = PreRoll ?? d.PreRoll,
        PostRoll = PostRoll ?? d.PostRoll,
        FallbackBreaks = d.FallbackBreaks,
        ColdOpenBreak = d.ColdOpenBreak,
    };

    public PaddingPolicy PaddingPolicy(SlotDefaults d) => new()
    {
        Mode = Pad is null ? d.PaddingMode : SlotDefaults.ParsePad(Pad),
    };
}

public enum OrderMode
{
    Chronological,
    Shuffle,
}
