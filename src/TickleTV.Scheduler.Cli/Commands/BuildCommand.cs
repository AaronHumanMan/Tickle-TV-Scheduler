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
        var config = new Option<FileInfo>("--config", "-c") { Description = "Scheduler YAML config.", DefaultValueFactory = _ => new FileInfo("tickletv.yaml") };
        var from = new Option<string?>("--from") { Description = "Window start, ISO 8601 (default: next whole minute)." };
        var hours = new Option<int?>("--hours") { Description = "Override every channel's horizon." };
        var channel = new Option<string?>("--channel") { Description = "Build only this channel number." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the plan, write nothing, change no state." };
        var seed = new Option<int?>("--seed") { Description = "Random seed for shuffle order (reproducible builds)." };

        var cmd = new Command("build", "Build playout windows for all channels and write them for Next.");
        foreach (Option o in new Option[] { config, from, hours, channel, dryRun, seed }) cmd.Options.Add(o);

        cmd.SetAction(async (p, ct) =>
        {
            SchedulerConfig cfg = SchedulerConfig.Load(p.GetRequiredValue(config).FullName);
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

                DateTimeOffset until = start.AddHours(p.GetValue(hours) ?? ch.HorizonHours);
                Console.WriteLine($"== Channel {ch.Number} ({ch.Name}) {start:yyyy-MM-dd HH:mm} to {until:yyyy-MM-dd HH:mm}{(dry ? " [dry run]" : "")}");

                db.BeginWork();
                BuildResult result;
                try
                {
                    var builder = new ChannelBuilder(db, picker, ch, cfg.Next.SchemaVersion, Console.WriteLine);
                    result = builder.Build(start, until);
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

                foreach (PlayRecord play in result.Plays)
                    db.RecordPlay(play.ItemId, ch.Number, play.AiredAt, play.Context);

                string playoutFolder = lc.PlayoutFolder;
                Directory.CreateDirectory(playoutFolder);
                RemoveOverlappingWindows(playoutFolder, start);
                string playoutPath = Path.Combine(playoutFolder, PlayoutJson.FileName(result.Start, result.End));
                await WriteAtomicAsync(playoutPath, PlayoutJson.Serialize(result.Playout), ct);

                string tvgId = string.IsNullOrWhiteSpace(ch.TvgId) ? lc.TvgId ?? $"tickle.{ch.Number}" : ch.TvgId;
                if (lineup.XmltvFolder is { } xmltvFolder)
                {
                    Directory.CreateDirectory(xmltvFolder);
                    string xmltvPath = Path.Combine(xmltvFolder, XmltvWriter.FileName(tvgId));
                    var merged = MergeGuide(xmltvPath, start, result.Programmes);
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

    /// <summary>Drops playout files that reach into the new window; the new build owns everything from its start.</summary>
    private static void RemoveOverlappingWindows(string folder, DateTimeOffset start)
    {
        foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            int underscore = name.IndexOf('_');
            if (underscore < 0) continue;
            if (TryParseCompact(name[(underscore + 1)..], out DateTimeOffset finish) && finish > start)
                File.Delete(file);
        }
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
