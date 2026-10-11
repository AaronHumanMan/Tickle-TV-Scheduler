using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;
using TickleTV.Scheduler.Core.Guide;
using TickleTV.Scheduler.Core.Playout;

namespace TickleTV.Scheduler.Cli.Commands;

/// <summary>
/// Builds a rolling playout window for every channel in the config and writes it into Next's folders.
/// A rebuild replaces everything from the start time forward, so edits reach the air on the next build.
/// </summary>
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

        cmd.SetAction(async (p, ct) =>
        {
            SchedulerConfig cfg = SchedulerConfig.Load(ConfigLocator.Resolve(p.GetValue(config)));
            string lineupPath = cfg.ResolvePath(cfg.Next.Lineup);
            Lineup lineup = ReadLineup(lineupPath);
            bool dry = p.GetValue(dryRun);

            using var db = new CatalogDb(cfg.ResolvePath(cfg.Catalog));
            var picker = new EpisodePicker(db, p.GetValue(seed));
            DateTimeOffset start = ParseStart(p.GetValue(from));
            int exit = 0;

            foreach (ChannelConfig ch in cfg.Channels)
            {
                if (p.GetValue(channel) is { } only && only != ch.Number) continue;
                if (!lineup.Channels.TryGetValue(ch.Number, out LineupChannel? lc))
                {
                    Console.Error.WriteLine($"Channel {ch.Number} is not in {lineupPath}.");
                    exit = 2;
                    continue;
                }

                // Cut in at the end of whatever is airing now: the current show finishes as planned, the new
                // schedule begins at its end, and the items before the cut are carried into the new file.
                string playoutFolder = lc.PlayoutFolder;
                string tvgId = string.IsNullOrWhiteSpace(ch.TvgId) ? lc.TvgId ?? $"tickle.{ch.Number}" : ch.TvgId;
                string? xmltvPath = lineup.XmltvFolder is { } xf ? Path.Combine(xf, XmltvWriter.FileName(tvgId)) : null;
                (DateTimeOffset cut, Playout? carried, string? carriedFile) = FindCutIn(playoutFolder, xmltvPath, start);

                DateTimeOffset until = cut.AddHours(p.GetValue(hours) ?? ch.HorizonHours);
                Console.WriteLine($"== Channel {ch.Number} ({ch.Name}) {cut:yyyy-MM-dd HH:mm} to {until:yyyy-MM-dd HH:mm}{(dry ? " [dry run]" : "")}");
                if (cut != start) Console.WriteLine($"   (joining at the end of the programme on air; {carried?.Items.Count(i => i.Finish <= cut) ?? 0} earlier items carried over)");

                // A rebuild keeps the episodes the previous window already assigned from this point on.
                var prior = new Dictionary<string, Queue<long>>(StringComparer.OrdinalIgnoreCase);
                foreach ((long itemId, _, string context) in p.GetValue(fresh) ? [] : db.ListSlotPlays(ch.Number, cut))
                {
                    if (ChannelBuilder.ShowFromSlotContext(context) is not { } show) continue;
                    if (!prior.TryGetValue(show, out Queue<long>? q)) prior[show] = q = new Queue<long>();
                    q.Enqueue(itemId);
                }

                db.BeginWork();
                BuildResult result;
                try
                {
                    var builder = new ChannelBuilder(db, picker, ch, cfg.Next.SchemaVersion, Console.WriteLine) { PriorAssignments = prior };
                    result = builder.Build(cut, until);
                }
                catch
                {
                    db.RollbackWork();
                    throw;
                }

                foreach (string w in result.Warnings) Console.WriteLine($"WARNING: {w}");

                if (dry)
                {
                    db.RollbackWork();
                    Console.WriteLine($"(dry run) {result.Playout.Items.Count} items, {result.Programmes.Count} programmes, nothing written.");
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
                await WriteAtomicAsync(playoutPath, PlayoutJson.Serialize(playout), ct);
                if (carriedFile is not null && !string.Equals(carriedFile, playoutPath, StringComparison.OrdinalIgnoreCase) && File.Exists(carriedFile))
                    File.Delete(carriedFile);

                if (xmltvPath is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(xmltvPath)!);
                    var merged = MergeGuide(xmltvPath, cut, result.Programmes);
                    await WriteAtomicAsync(xmltvPath, XmltvWriter.Write(tvgId, merged), ct);
                }

                db.CommitWork();
                Console.WriteLine($"Wrote {result.Playout.Items.Count} items to {playoutPath}");
            }
            return exit;
        });
        return cmd;
    }

    private sealed record LineupChannel(string Number, string? TvgId, string PlayoutFolder);
    private sealed record Lineup(string? XmltvFolder, Dictionary<string, LineupChannel> Channels);

    /// <summary>Reads lineup.json and each channel.json to find the playout and xmltv folders.</summary>
    private static Lineup ReadLineup(string lineupPath)
    {
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

    /// <summary>
    /// Where a rebuild joins the running schedule: the stop time of the guide programme that contains
    /// <paramref name="start"/>, when an existing window covers it; otherwise <paramref name="start"/> itself.
    /// Also returns that window's playout so its items before the cut can be carried into the new file.
    /// </summary>
    private static (DateTimeOffset Cut, Playout? Carried, string? File) FindCutIn(string playoutFolder, string? xmltvPath, DateTimeOffset start)
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

        Playout carried;
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
        // The cut must land on an item boundary of the carried window; otherwise fall back to the window start.
        if (!carried.Items.Any(i => i.Finish == cut)) cut = start;
        return (cut, carried, covering);
    }

    /// <summary>Parses Next's compact file-name timestamp, e.g. <c>20261010T234000.000000000-0500</c>.</summary>
    public static bool TryParseCompact(string s, out DateTimeOffset value)
    {
        value = default;
        // Layout: 15 chars date-time, '.', 9 fraction digits, then +HHmm or -HHmm.
        if (s.Length != 30 || s[15] != '.' || (s[25] != '+' && s[25] != '-')) return false;
        if (!DateTime.TryParseExact(s[..23], "yyyyMMdd'T'HHmmss.fffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local)) return false;
        if (!int.TryParse(s.AsSpan(26, 2), out int oh) || !int.TryParse(s.AsSpan(28, 2), out int om)) return false;
        TimeSpan offset = new TimeSpan(oh, om, 0) * (s[25] == '-' ? -1 : 1);
        value = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset);
        return true;
    }

    /// <summary>Keeps programmes that ended before the new window and appends the new ones.</summary>
    private static List<GuideProgramme> MergeGuide(string xmltvPath, DateTimeOffset start, IReadOnlyList<GuideProgramme> fresh)
    {
        var kept = new List<GuideProgramme>();
        if (File.Exists(xmltvPath))
        {
            XDocument doc = XDocument.Load(xmltvPath);
            foreach (XElement p in doc.Root?.Elements("programme") ?? [])
            {
                if (!TryParseXmltv(p.Attribute("start")?.Value, out DateTimeOffset s) || !TryParseXmltv(p.Attribute("stop")?.Value, out DateTimeOffset e)) continue;
                if (e > start) continue;
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

    private static DateTimeOffset ParseStart(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
        DateTimeOffset now = DateTimeOffset.Now;
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).AddMinutes(1);
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        string tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, path, overwrite: true);
    }
}
