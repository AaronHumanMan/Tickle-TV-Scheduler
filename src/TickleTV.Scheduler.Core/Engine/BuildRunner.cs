using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Guide;
using TickleTV.Scheduler.Core.Playout;

namespace TickleTV.Scheduler.Core.Engine;

public sealed record BuildOptions
{
    /// <summary>Window start; null means the next whole minute.</summary>
    public DateTimeOffset? From { get; init; }
    /// <summary>Overrides every channel's horizon.</summary>
    public int? Hours { get; init; }
    /// <summary>Build only this channel number.</summary>
    public string? Channel { get; init; }
    /// <summary>Plan only: write nothing, change no state.</summary>
    public bool DryRun { get; init; }
    public int? Seed { get; init; }
    /// <summary>Ignore the episodes the existing window already assigned.</summary>
    public bool Fresh { get; init; }
}

public sealed record ChannelBuildReport
{
    public required string Channel { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public required int ItemCount { get; init; }
    public required int CarriedItems { get; init; }
    public required IReadOnlyList<GuideProgramme> Programmes { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public string? PlayoutPath { get; init; }
    public string? GuidePath { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Builds every channel in a config and writes the results for Next: the playout window, the merged guide,
/// plays, and rotation. A rebuild joins the running schedule at the end of the programme on air and keeps
/// the episodes the old window had already assigned. Used by the CLI and the web app alike.
/// </summary>
public static class BuildRunner
{
    public static List<ChannelBuildReport> Run(SchedulerConfig cfg, BuildOptions options, Action<string>? log = null)
    {
        string lineupPath = cfg.ResolvePath(cfg.Next.Lineup);
        Lineup lineup = ReadLineup(lineupPath);
        var reports = new List<ChannelBuildReport>();

        using var db = new CatalogDb(cfg.ResolvePath(cfg.Catalog));
        var picker = new EpisodePicker(db, options.Seed);
        DateTimeOffset start = options.From ?? NextWholeMinute();

        foreach (ChannelConfig ch in cfg.Channels)
        {
            if (options.Channel is { } only && only != ch.Number) continue;
            if (!lineup.Channels.TryGetValue(ch.Number, out LineupChannel? lc))
            {
                reports.Add(Failed(ch, start, $"Channel {ch.Number} is not in {lineupPath}."));
                log?.Invoke($"Channel {ch.Number} is not in {lineupPath}.");
                continue;
            }

            string playoutFolder = lc.PlayoutFolder;
            string tvgId = string.IsNullOrWhiteSpace(ch.TvgId) ? lc.TvgId ?? $"tickle.{ch.Number}" : ch.TvgId;
            string? xmltvPath = lineup.XmltvFolder is { } xf ? Path.Combine(xf, XmltvWriter.FileName(tvgId)) : null;
            (DateTimeOffset cut, Playout.Playout? carried, string? carriedFile) = FindCutIn(playoutFolder, xmltvPath, start);
            int carriedCount = carried?.Items.Count(i => i.Finish <= cut) ?? 0;

            DateTimeOffset until = cut.AddHours(options.Hours ?? ch.HorizonHours);
            log?.Invoke($"== Channel {ch.Number} ({ch.Name}) {cut:yyyy-MM-dd HH:mm} to {until:yyyy-MM-dd HH:mm}{(options.DryRun ? " [dry run]" : "")}");
            if (cut != start) log?.Invoke($"   (joining at the end of the programme on air; {carriedCount} earlier items carried over)");

            var prior = new Dictionary<string, Queue<long>>(StringComparer.OrdinalIgnoreCase);
            if (!options.Fresh)
            {
                foreach ((long itemId, _, string context) in db.ListSlotPlays(ch.Number, cut))
                {
                    if (ChannelBuilder.ShowFromSlotContext(context) is not { } show) continue;
                    if (!prior.TryGetValue(show, out Queue<long>? q)) prior[show] = q = new Queue<long>();
                    q.Enqueue(itemId);
                }
            }

            db.BeginWork();
            BuildResult result;
            try
            {
                var builder = new ChannelBuilder(db, picker, ch, cfg.Next.SchemaVersion, log) { PriorAssignments = prior };
                result = builder.Build(cut, until);
            }
            catch (Exception ex)
            {
                db.RollbackWork();
                reports.Add(Failed(ch, cut, ex.Message));
                log?.Invoke($"ERROR: {ex.Message}");
                continue;
            }

            foreach (string w in result.Warnings) log?.Invoke($"WARNING: {w}");

            if (options.DryRun)
            {
                db.RollbackWork();
                log?.Invoke($"(dry run) {result.Playout.Items.Count} items, {result.Programmes.Count} programmes, nothing written.");
                reports.Add(new ChannelBuildReport
                {
                    Channel = ch.Number, Name = ch.Name, Start = cut, End = result.End, ItemCount = result.Playout.Items.Count,
                    CarriedItems = carriedCount, Programmes = result.Programmes, Warnings = result.Warnings,
                });
                continue;
            }

            db.DeletePlays(ch.Number, cut);
            foreach (PlayRecord play in result.Plays)
                db.RecordPlay(play.ItemId, ch.Number, play.AiredAt, play.Context);

            Directory.CreateDirectory(playoutFolder);
            List<PlayoutItem> items = [.. carried?.Items.Where(i => i.Finish <= cut) ?? [], .. result.Playout.Items];
            DateTimeOffset fileStart = items[0].Start;
            var playout = result.Playout with { Items = items };
            string playoutPath = Path.Combine(playoutFolder, PlayoutJson.FileName(fileStart, result.End));
            RemoveOverlappingWindows(playoutFolder, cut, keep: playoutPath);
            WriteAtomic(playoutPath, PlayoutJson.Serialize(playout));
            if (carriedFile is not null && !string.Equals(carriedFile, playoutPath, StringComparison.OrdinalIgnoreCase) && File.Exists(carriedFile))
                File.Delete(carriedFile);

            if (xmltvPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(xmltvPath)!);
                var merged = MergeGuide(xmltvPath, cut, result.Programmes);
                WriteAtomic(xmltvPath, XmltvWriter.Write(tvgId, merged));
            }

            db.CommitWork();
            log?.Invoke($"Wrote {result.Playout.Items.Count} items to {playoutPath}");
            reports.Add(new ChannelBuildReport
            {
                Channel = ch.Number, Name = ch.Name, Start = cut, End = result.End, ItemCount = result.Playout.Items.Count,
                CarriedItems = carriedCount, Programmes = result.Programmes, Warnings = result.Warnings,
                PlayoutPath = playoutPath, GuidePath = xmltvPath,
            });
        }
        return reports;
    }

    private static ChannelBuildReport Failed(ChannelConfig ch, DateTimeOffset start, string error) => new()
    {
        Channel = ch.Number, Name = ch.Name, Start = start, End = start, ItemCount = 0, CarriedItems = 0,
        Programmes = [], Warnings = [], Error = error,
    };

    public static DateTimeOffset NextWholeMinute()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).AddMinutes(1);
    }

    private sealed record LineupChannel(string Number, string? TvgId, string PlayoutFolder);
    private sealed record Lineup(string? XmltvFolder, Dictionary<string, LineupChannel> Channels);

    /// <summary>Reads lineup.json and each channel.json to find the playout and xmltv folders.</summary>
    private static Lineup ReadLineup(string lineupPath)
    {
        if (!File.Exists(lineupPath)) throw new FileNotFoundException($"Next's lineup file was not found at {lineupPath}. Check next.lineup in the config.", lineupPath);
        string lineupDir = Path.GetDirectoryName(lineupPath)!;
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(lineupPath));
        JsonElement root = doc.RootElement;

        string? xmltv = null;
        if (root.TryGetProperty("xmltv", out JsonElement x) && x.ValueKind == JsonValueKind.Object && x.TryGetProperty("folder", out JsonElement xf))
            xmltv = Resolve(lineupDir, xf.GetString()!);

        var channels = new Dictionary<string, LineupChannel>();
        foreach (JsonElement c in root.GetProperty("channels").EnumerateArray())
        {
            string number = c.GetProperty("number").GetString()!;
            string? tvg = c.TryGetProperty("tvg_id", out JsonElement t) ? t.GetString() : null;
            string channelJson = Resolve(lineupDir, c.GetProperty("config").GetString()!);
            string channelDir = Path.GetDirectoryName(channelJson)!;

            string playoutFolder = "./playout";
            using JsonDocument cdoc = JsonDocument.Parse(File.ReadAllText(channelJson));
            if (cdoc.RootElement.TryGetProperty("playout", out JsonElement pl) && pl.TryGetProperty("folder", out JsonElement pf) && pf.GetString() is { Length: > 0 } s)
                playoutFolder = s;
            channels[number] = new LineupChannel(number, tvg, Resolve(channelDir, playoutFolder));
        }
        return new Lineup(xmltv, channels);
    }

    private static string Resolve(string baseDir, string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDir, path));

    /// <summary>Drops playout files that reach past the cut; the new build owns everything from there.</summary>
    private static void RemoveOverlappingWindows(string folder, DateTimeOffset cut, string? keep = null)
    {
        foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (keep is not null && string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) continue;
            string name = Path.GetFileNameWithoutExtension(file);
            int underscore = name.IndexOf('_');
            if (underscore < 0) continue;
            if (TryParseCompact(name[(underscore + 1)..], out DateTimeOffset finish) && finish > cut)
                File.Delete(file);
        }
    }

    /// <summary>Parses Next's compact file-name timestamp, e.g. <c>20261010T234000.000000000-0500</c>.</summary>
    public static bool TryParseCompact(string s, out DateTimeOffset value)
    {
        value = default;
        if (s.Length != 30 || s[15] != '.' || (s[25] != '+' && s[25] != '-')) return false;
        if (!DateTime.TryParseExact(s[..23], "yyyyMMdd'T'HHmmss.fffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local)) return false;
        if (!int.TryParse(s.AsSpan(26, 2), out int oh) || !int.TryParse(s.AsSpan(28, 2), out int om)) return false;
        TimeSpan offset = new TimeSpan(oh, om, 0) * (s[25] == '-' ? -1 : 1);
        value = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset);
        return true;
    }

    /// <summary>
    /// Where a rebuild joins the running schedule: the stop time of the guide programme that contains
    /// <paramref name="start"/>, when an existing window covers it; otherwise <paramref name="start"/> itself.
    /// </summary>
    private static (DateTimeOffset Cut, Playout.Playout? Carried, string? File) FindCutIn(string playoutFolder, string? xmltvPath, DateTimeOffset start)
    {
        if (!Directory.Exists(playoutFolder)) return (start, null, null);
        string? covering = null;
        foreach (string file in Directory.EnumerateFiles(playoutFolder, "*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            int underscore = name.IndexOf('_');
            if (underscore < 0) continue;
            if (TryParseCompact(name[..underscore], out DateTimeOffset s) && TryParseCompact(name[(underscore + 1)..], out DateTimeOffset f) && s <= start && f > start)
                covering = file;
        }
        if (covering is null) return (start, null, null);

        Playout.Playout carried;
        try { carried = PlayoutJson.Deserialize(File.ReadAllText(covering)); }
        catch (Exception) { return (start, null, null); }

        DateTimeOffset cut = start;
        if (xmltvPath is not null && File.Exists(xmltvPath))
        {
            XDocument doc = XDocument.Load(xmltvPath);
            foreach (XElement p in doc.Root?.Elements("programme") ?? [])
            {
                if (TryParseXmltv(p.Attribute("start")?.Value, out DateTimeOffset ps) && TryParseXmltv(p.Attribute("stop")?.Value, out DateTimeOffset pe) && ps <= start && pe > start)
                {
                    cut = pe;
                    break;
                }
            }
        }
        if (!carried.Items.Any(i => i.Finish == cut)) cut = start;
        return (cut, carried, covering);
    }

    /// <summary>Keeps programmes that ended before the cut and appends the new ones.</summary>
    private static List<GuideProgramme> MergeGuide(string xmltvPath, DateTimeOffset cut, IReadOnlyList<GuideProgramme> fresh)
    {
        var kept = new List<GuideProgramme>();
        if (File.Exists(xmltvPath))
        {
            XDocument doc = XDocument.Load(xmltvPath);
            foreach (XElement p in doc.Root?.Elements("programme") ?? [])
            {
                if (!TryParseXmltv(p.Attribute("start")?.Value, out DateTimeOffset s) || !TryParseXmltv(p.Attribute("stop")?.Value, out DateTimeOffset e)) continue;
                if (e > cut) continue;
                kept.Add(new GuideProgramme
                {
                    Start = s,
                    Stop = e,
                    Title = p.Element("title")?.Value ?? "",
                    SubTitle = p.Element("sub-title")?.Value,
                    Description = p.Element("desc")?.Value,
                    Category = p.Element("category")?.Value,
                    EpisodeNumber = p.Element("episode-num")?.Value,
                });
            }
        }
        kept.AddRange(fresh);
        return kept;
    }

    private static bool TryParseXmltv(string? s, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(s, "yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    private static void WriteAtomic(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
