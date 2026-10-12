using TickleTV.Scheduler.Core.Engine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.Globalization;

namespace TickleTV.Scheduler.Core.Config;

/// <summary>Top-level YAML config. Paths are resolved relative to the config file's folder.</summary>
public sealed class SchedulerConfig
{
    public string Catalog { get; set; } = "tickletv.sqlite";
    /// <summary>Folders the app scans. The CLI can scan any folder; the app scans these.</summary>
    public List<string> MediaRoots { get; set; } = [];
    public NextConfig Next { get; set; } = new();
    public UiConfig Ui { get; set; } = new();
    /// <summary>The nightly automatic build the app runs on its own.</summary>
    public AutoBuildConfig AutoBuild { get; set; } = new();
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
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
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
            ch.NormalizePlans();
            if (ch.Plans.Count == 0 || ch.Plans.All(p => p.Slots.Count == 0)) throw new InvalidDataException($"Channel {ch.Number} has no schedule slots.");
            foreach (DayPlan plan in ch.Plans)
            {
                if (string.IsNullOrWhiteSpace(plan.Name)) throw new InvalidDataException($"Channel {ch.Number}: every day plan needs a name.");
                if (plan.Slots.Count == 0) throw new InvalidDataException($"Channel {ch.Number}: the day plan '{plan.Name}' has no time slots.");
            }
            foreach (string dup in ch.Plans.GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key))
                throw new InvalidDataException($"Channel {ch.Number}: two day plans are both called '{dup}'.");
            if (ch.DefaultPlan is not null && ch.FindPlan(ch.DefaultPlan) is null)
                throw new InvalidDataException($"Channel {ch.Number}: the default day plan '{ch.DefaultPlan}' does not exist.");
            foreach (CalendarRule rule in ch.Calendar) rule.Validate(ch);
            ch.Ads.Normalize();
            foreach (SlotConfig slot in ch.Plans.SelectMany(p => p.Slots))
            {
                slot.Normalize();
                if (slot.ShowList.Count == 0 || slot.ShowList.Any(string.IsNullOrWhiteSpace))
                    throw new InvalidDataException($"Channel {ch.Number}: every slot needs at least one show.");
                _ = slot.StartTime; // throws on a bad time
                _ = slot.OrderMode;
                _ = slot.RandomTurns;
            }
            _ = ch.Defaults.PaddingMode;
        }
    }
}

/// <summary>How the app shows things; harmless defaults, saved when the person changes them.</summary>
public sealed class UiConfig
{
    /// <summary>list or grid.</summary>
    public string ScheduleView { get; set; } = "list";
    /// <summary>list or grid.</summary>
    public string BuildView { get; set; } = "list";
    /// <summary>50, 100, 150, or 200.</summary>
    public int PageSize { get; set; } = 50;
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

    /// <summary>The channel's day plans: each a full daily grid. The calendar says which one each date follows.</summary>
    public List<DayPlan> Plans { get; set; } = [];
    /// <summary>Which plan a date follows when no rule says otherwise. Null means the first plan.</summary>
    public string? DefaultPlan { get; set; }
    /// <summary>Rules that pick a plan for dates: weekdays, holidays, yearly ranges, or days pinned on the calendar.</summary>
    public List<CalendarRule> Calendar { get; set; } = [];

    /// <summary>The old single-grid form, <c>schedule:</c>. Moved into <see cref="Plans"/> by <see cref="NormalizePlans"/>.</summary>
    [YamlMember(Alias = "schedule")]
    public List<SlotConfig>? LegacySchedule { get; set; }

    /// <summary>The default plan's slots: the grid the channel follows on an ordinary day.</summary>
    [YamlIgnore]
    public List<SlotConfig> Schedule
    {
        get => Default.Slots;
        set => Default.Slots = value;
    }

    /// <summary>The default day plan, created as "Every day" when the channel has none yet.</summary>
    [YamlIgnore]
    public DayPlan Default
    {
        get
        {
            NormalizePlans();
            if (Plans.Count == 0) Plans.Add(new DayPlan { Name = DayPlan.DefaultName });
            return (DefaultPlan is null ? null : FindPlan(DefaultPlan)) ?? Plans[0];
        }
    }

    public DayPlan? FindPlan(string? name) =>
        name is null ? null : Plans.FirstOrDefault(p => string.Equals(p.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The plan that holds this slot, for anything that needs the slot's neighbours.</summary>
    public DayPlan? PlanOf(SlotConfig slot) => Plans.FirstOrDefault(p => p.Slots.Contains(slot));

    /// <summary>Folds the old <c>schedule:</c> list into the plans so the file always has one shape.</summary>
    public void NormalizePlans()
    {
        if (LegacySchedule is null) return;
        if (LegacySchedule.Count > 0)
        {
            DayPlan? target = FindPlan(DayPlan.DefaultName);
            if (target is null) Plans.Insert(0, target = new DayPlan { Name = DayPlan.DefaultName });
            target.Slots = LegacySchedule;
        }
        LegacySchedule = null;
    }

    /// <summary>Renames a plan everywhere it is referred to: the default and the calendar rules.</summary>
    public void RenamePlan(DayPlan plan, string newName)
    {
        string old = plan.Name;
        plan.Name = newName.Trim();
        if (string.Equals(DefaultPlan, old, StringComparison.OrdinalIgnoreCase)) DefaultPlan = plan.Name;
        foreach (CalendarRule r in Calendar.Where(r => string.Equals(r.Plan, old, StringComparison.OrdinalIgnoreCase))) r.Plan = plan.Name;
    }

    /// <summary>Removes a plan and every rule that pointed at it. The first remaining plan becomes the default if needed.</summary>
    public void RemovePlan(DayPlan plan)
    {
        Plans.Remove(plan);
        Calendar.RemoveAll(r => string.Equals(r.Plan, plan.Name, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(DefaultPlan, plan.Name, StringComparison.OrdinalIgnoreCase)) DefaultPlan = null;
    }
}

/// <summary>A named 24-hour grid. A channel follows one plan per day; the calendar picks which.</summary>
public sealed class DayPlan
{
    public const string DefaultName = "Every day";
    public string Name { get; set; } = DefaultName;
    public List<SlotConfig> Slots { get; set; } = [];

    /// <summary>A deep copy with a new name, for "make a copy of this plan".</summary>
    public DayPlan Copy(string name) => new()
    {
        Name = name,
        Slots = Slots.Select(s => new SlotConfig
        {
            Time = s.Time, Shows = [.. s.ShowList], Turns = s.Turns, Order = s.Order, Pad = s.Pad, Holiday = s.Holiday, HolidayEpisodes = s.HolidayEpisodes,
            MaxBreaks = s.MaxBreaks, MinActSeconds = s.MinActSeconds, PreRoll = s.PreRoll, PostRoll = s.PostRoll,
        }).ToList(),
    };
}

public enum RuleKind { Weekdays, Holiday, Range, Dates, Empty }

/// <summary>
/// One calendar rule: "plan X on these weekdays", "plan X for this holiday (and days around it)",
/// "plan X from MM-dd to MM-dd every year", or "plan X on these exact dates" (pinned on the calendar page).
/// </summary>
public sealed class CalendarRule
{
    public string Plan { get; set; } = "";
    /// <summary>monday .. sunday</summary>
    public List<string> Days { get; set; } = [];
    /// <summary>A key from <see cref="Holidays.All"/>, such as christmas or thanksgiving.</summary>
    public string? Holiday { get; set; }
    public int? DaysBefore { get; set; }
    public int? DaysAfter { get; set; }
    /// <summary>MM-dd, inclusive, every year. A range may wrap past New Year (12-20 to 01-03).</summary>
    public string? From { get; set; }
    public string? To { get; set; }
    /// <summary>yyyy-MM-dd</summary>
    public List<string> Dates { get; set; } = [];

    [YamlIgnore]
    public RuleKind Kind =>
        Dates.Count > 0 ? RuleKind.Dates
        : Holiday is not null ? RuleKind.Holiday
        : From is not null || To is not null ? RuleKind.Range
        : Days.Count > 0 ? RuleKind.Weekdays
        : RuleKind.Empty;

    [YamlIgnore]
    public IReadOnlySet<DateOnly> ParsedDates => Dates
        .Select(d => DateOnly.TryParseExact(d.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly x) ? x : (DateOnly?)null)
        .Where(d => d is not null).Select(d => d!.Value).ToHashSet();

    public bool Covers(DateOnly date) => Kind switch
    {
        RuleKind.Weekdays => Days.Any(d => string.Equals(d.Trim(), date.DayOfWeek.ToString(), StringComparison.OrdinalIgnoreCase)),
        RuleKind.Holiday => HolidaySpan(date.Year) is { } span && date >= span.Start && date <= span.End
                            || HolidaySpan(date.Year - 1) is { } prev && date >= prev.Start && date <= prev.End
                            || HolidaySpan(date.Year + 1) is { } next && date >= next.Start && date <= next.End,
        RuleKind.Range => InRange(date),
        RuleKind.Dates => ParsedDates.Contains(date),
        _ => false,
    };

    /// <summary>How many days the rule covers in a year; shorter rules are more specific and win.</summary>
    public int SpanDays(int year) => Kind switch
    {
        RuleKind.Holiday => 1 + (DaysBefore ?? 0) + (DaysAfter ?? 0),
        RuleKind.Range when ParseMonthDay(From) is { } f && ParseMonthDay(To) is { } t =>
            (int)(ToDate(t, year) < ToDate(f, year) ? ToDate(t, year + 1).DayNumber - ToDate(f, year).DayNumber : ToDate(t, year).DayNumber - ToDate(f, year).DayNumber) + 1,
        RuleKind.Dates => Dates.Count,
        _ => 366,
    };

    public (DateOnly Start, DateOnly End)? HolidaySpan(int year)
    {
        if (Holidays.Find(Holiday) is not { } h) return null;
        DateOnly day = h.On(year);
        return (day.AddDays(-(DaysBefore ?? 0)), day.AddDays(DaysAfter ?? 0));
    }

    private bool InRange(DateOnly date)
    {
        if (ParseMonthDay(From) is not { } f || ParseMonthDay(To) is not { } t) return false;
        DateOnly start = ToDate(f, date.Year), end = ToDate(t, date.Year);
        if (end >= start) return date >= start && date <= end;
        // Wraps past New Year: Dec 20 to Jan 3 covers late December and early January.
        return date >= start || date <= end;
    }

    public static (int Month, int Day)? ParseMonthDay(string? text)
    {
        if (text is null) return null;
        string[] parts = text.Trim().Split('-', '/');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int m) || !int.TryParse(parts[1], out int d)) return null;
        if (m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(2000, m)) return null;
        return (m, d);
    }

    private static DateOnly ToDate((int Month, int Day) md, int year) =>
        new(year, md.Month, Math.Min(md.Day, DateTime.DaysInMonth(year, md.Month)));

    public void Validate(ChannelConfig ch)
    {
        if (Kind == RuleKind.Empty) throw new InvalidDataException($"Channel {ch.Number}: a calendar rule for '{Plan}' does not say when it applies.");
        if (ch.FindPlan(Plan) is null) throw new InvalidDataException($"Channel {ch.Number}: a calendar rule points at a day plan '{Plan}' that does not exist.");
        foreach (string d in Days)
            if (!PlanCalendar.DayKeys.Contains(d.Trim().ToLowerInvariant()))
                throw new InvalidDataException($"Channel {ch.Number}: '{d}' is not a day of the week. Use monday to sunday.");
        if (Holiday is not null && Holidays.Find(Holiday) is null)
            throw new InvalidDataException($"Channel {ch.Number}: unknown holiday '{Holiday}'. Known: {string.Join(", ", Holidays.All.Select(h => h.Key))}.");
        if (Kind == RuleKind.Range && (ParseMonthDay(From) is null || ParseMonthDay(To) is null))
            throw new InvalidDataException($"Channel {ch.Number}: a date range needs both from and to as MM-dd, such as 12-18 and 12-26.");
        if (Kind == RuleKind.Dates && ParsedDates.Count != Dates.Count)
            throw new InvalidDataException($"Channel {ch.Number}: a pinned date is not in yyyy-MM-dd form.");
    }
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
    /// <summary>Category slugs that never air anywhere on this channel.</summary>
    public List<string> Never { get; set; } = [];

    public void Normalize() => Never = AdRules.CleanSlugs(Never);
}

public sealed class SlotConfig
{
    /// <summary>Daily start time, HH:mm. The slot runs until the next slot's start.</summary>
    public string Time { get; set; } = "00:00";
    /// <summary>One show (the simple form). Moved into <see cref="Shows"/> by <see cref="Normalize"/>.</summary>
    public string? Show { get; set; }
    /// <summary>The shows that share this block. They take turns, one episode each, in this order (or at random, see <see cref="Turns"/>).</summary>
    public List<string> Shows { get; set; } = [];
    /// <summary>How a block picks the next show: in_order (down the list, carrying over day to day) or random (never the same show twice in a row). Null means in_order.</summary>
    public string? Turns { get; set; }
    /// <summary>chronological or shuffle; applies to every show in the block.</summary>
    public string Order { get; set; } = "chronological";
    /// <summary>A holiday key (christmas, halloween, ...) when the slot favours holiday episodes. Null means any episode.</summary>
    public string? Holiday { get; set; }
    /// <summary>any (default), first (each holiday episode once, then regular ones) or only (holiday episodes, wrapping).</summary>
    public string? HolidayEpisodes { get; set; }
    /// <summary>Which commercial categories may air in this slot. Null means any.</summary>
    public SlotAdsConfig? Ads { get; set; }
    public string? Pad { get; set; }
    public int? MaxBreaks { get; set; }
    public int? MinActSeconds { get; set; }
    public bool? PreRoll { get; set; }
    public bool? PostRoll { get; set; }

    /// <summary>Every show in the slot, whichever form the file used.</summary>
    [YamlIgnore]
    public IReadOnlyList<string> ShowList => Shows.Count > 0 ? Shows : string.IsNullOrWhiteSpace(Show) ? [] : [Show.Trim()];

    /// <summary>"The Simpsons" or "Cheers / Frasier / Wings" for a block.</summary>
    [YamlIgnore]
    public string Label => string.Join(" / ", ShowList);

    /// <summary>Folds the single-show form into the list so the file always has one shape.</summary>
    public void Normalize()
    {
        if (Shows.Count == 0 && !string.IsNullOrWhiteSpace(Show)) Shows = [Show.Trim()];
        Show = null;
        // Only a block with random turns needs the line in the file.
        Turns = RandomTurns && Shows.Count > 1 ? "random" : null;
        // Likewise a holiday choice: "any" is the default and stays out of the file.
        if (HolidayMode == HolidayMode.Any || string.IsNullOrWhiteSpace(Holiday)) { Holiday = null; HolidayEpisodes = null; }
        else
        {
            Holiday = Holiday.Trim().ToLowerInvariant();
            if (Holidays.Find(Holiday) is null) throw new InvalidDataException($"Unknown holiday '{Holiday}'. Known: {string.Join(", ", Holidays.All.Select(h => h.Key))}.");
            HolidayEpisodes = HolidayMode == HolidayMode.Only ? "only" : "first";
        }
        // Category rules: tidy the slugs, and keep an empty rule out of the file.
        Ads?.Normalize();
        if (Ads is { IsEmpty: true }) Ads = null;
    }

    [YamlIgnore]
    public TimeOnly StartTime => TimeOnly.TryParseExact(Time.Trim(), ["H:mm", "HH:mm", "H:mm:ss"], out TimeOnly t)
        ? t
        : throw new InvalidDataException($"Bad slot time '{Time}'. Use HH:mm.");

    [YamlIgnore]
    public HolidayMode HolidayMode => (HolidayEpisodes ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "any" => HolidayMode.Any,
        "first" or "prefer" => HolidayMode.First,
        "only" => HolidayMode.Only,
        _ => throw new InvalidDataException($"Unknown holiday_episodes '{HolidayEpisodes}'. Use any, first or only."),
    };

    /// <summary>"Christmas Day episodes first" or null when the slot takes any episode.</summary>
    [YamlIgnore]
    public string? HolidayWords => HolidayMode == HolidayMode.Any || Holiday is null ? null
        : $"{Holidays.Find(Holiday)?.Name ?? Holiday} episodes {(HolidayMode == HolidayMode.Only ? "only" : "first")}";

    [YamlIgnore]
    public bool RandomTurns => (Turns ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "in_order" or "in-order" or "inorder" or "list" => false,
        "random" or "shuffle" => true,
        _ => throw new InvalidDataException($"Unknown turns '{Turns}'. Use in_order or random."),
    };

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

/// <summary>How a slot treats holiday episodes of its shows.</summary>
public enum HolidayMode
{
    Any,
    /// <summary>Each holiday episode once (in the slot's order), then the regular rotation. Starts afresh each year.</summary>
    First,
    /// <summary>Holiday episodes and nothing else, wrapping round.</summary>
    Only,
}

public enum OrderMode
{
    Chronological,
    Shuffle,
}
