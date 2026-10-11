using System.CommandLine;
using System.Text.Json;
using TickleTV.Scheduler.Core.Engine;
using TickleTV.Scheduler.Core.Guide;
using TickleTV.Scheduler.Core.Media;
using TickleTV.Scheduler.Core.Playout;

namespace TickleTV.Scheduler.Cli.Commands;

/// <summary>
/// Phase 0 spike. Writes one playout window for a single episode into a Next channel folder so the
/// in/out-point cuts, slate filler, and XMLTV contract can be checked against a running Next.
/// </summary>
public static class SpikeCommand
{
    private static readonly string[] ClipExtensions = [".mp4", ".mkv", ".mov", ".ts", ".webm", ".m4v"];

    public static Command Create()
    {
        var file = new Option<FileInfo>("--file", "-f") { Description = "Episode to air.", Required = true };
        var channelDir = new Option<DirectoryInfo>("--channel-dir", "-c")
        {
            Description = "Next channel folder containing channel.json (playout folder is read from it).",
            Required = true,
        };
        var ads = new Option<DirectoryInfo?>("--ads") { Description = "Folder of ad/bumper clips used to fill breaks." };
        var xmltvDir = new Option<DirectoryInfo?>("--xmltv-dir") { Description = "Lineup xmltv folder to write guide data into." };
        var tvgId = new Option<string?>("--tvg-id") { Description = "Channel tvg_id from lineup.json (required with --xmltv-dir)." };
        var title = new Option<string?>("--title") { Description = "Guide title (default: file name)." };
        var start = new Option<string?>("--start") { Description = "Start time, ISO 8601 (default: next whole minute)." };
        var pad = new Option<string>("--pad") { Description = "Padding: 30, 60, 5, 10, or none.", DefaultValueFactory = _ => "30" };
        var maxBreaks = new Option<int>("--max-breaks") { Description = "Max mid-show breaks.", DefaultValueFactory = _ => 3 };
        var minAct = new Option<int>("--min-act") { Description = "Minimum act length in seconds.", DefaultValueFactory = _ => 120 };
        var preRoll = new Option<bool>("--pre-roll") { Description = "Add a break before the show." };
        var noPostRoll = new Option<bool>("--no-post-roll") { Description = "Skip the break after the show." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the plan but write nothing." };
        var ffprobe = new Option<string?>("--ffprobe") { Description = "Path to ffprobe (default: channel.json or PATH)." };

        var cmd = new Command("spike", "Write a one-episode playout window into a Next channel folder.");
        foreach (Option o in new Option[] { file, channelDir, ads, xmltvDir, tvgId, title, start, pad, maxBreaks, minAct, preRoll, noPostRoll, dryRun, ffprobe })
            cmd.Options.Add(o);

        cmd.SetAction(async (p, ct) =>
        {
            DirectoryInfo channel = p.GetRequiredValue(channelDir);
            string channelJsonPath = Path.Combine(channel.FullName, "channel.json");
            if (!File.Exists(channelJsonPath))
            {
                Console.Error.WriteLine($"channel.json not found in {channel.FullName}");
                return 2;
            }

            (string playoutFolder, string? ffprobeFromConfig) = ReadChannelConfig(channelJsonPath, channel.FullName);
            string? xmltvFolder = p.GetValue(xmltvDir)?.FullName;
            string? tvg = p.GetValue(tvgId);
            if (xmltvFolder is not null && string.IsNullOrWhiteSpace(tvg))
            {
                Console.Error.WriteLine("--tvg-id is required with --xmltv-dir");
                return 2;
            }

            var prober = new Ffprobe(p.GetValue(ffprobe) ?? ffprobeFromConfig);
            FileInfo episodeFile = p.GetRequiredValue(file);
            MediaProbe episode = await prober.ProbeAsync(episodeFile.FullName, ct);

            var clips = new List<FillerClip>();
            if (p.GetValue(ads) is { } adsDir && adsDir.Exists)
            {
                foreach (FileInfo clip in adsDir.EnumerateFiles().Where(f => ClipExtensions.Contains(f.Extension.ToLowerInvariant())))
                {
                    MediaProbe probe = await prober.ProbeAsync(clip.FullName, ct);
                    if (probe.DurationMs > 0) clips.Add(new FillerClip(clip.FullName, probe.DurationMs));
                }
            }

            DateTimeOffset startAt = ParseStart(p.GetValue(start));
            var request = new SpikeRequest
            {
                Episode = episode,
                Title = p.GetValue(title) ?? Path.GetFileNameWithoutExtension(episodeFile.Name),
                Start = startAt,
                Ads = clips,
                BreakPolicy = new BreakPolicy
                {
                    MaxMidBreaks = p.GetValue(maxBreaks),
                    MinActMs = p.GetValue(minAct) * 1000L,
                    PreRoll = p.GetValue(preRoll),
                    PostRoll = !p.GetValue(noPostRoll),
                    FallbackBreaks = episode.Chapters.Count > 1 ? 0 : 2,
                },
                PaddingPolicy = new PaddingPolicy { Mode = ParsePad(p.GetValue(pad)!) },
            };

            SpikeResult result = SpikeBuilder.Build(request);
            PrintPlan(result, clips.Count);

            if (p.GetValue(dryRun))
            {
                Console.WriteLine("\n(dry run, nothing written)");
                return 0;
            }

            Directory.CreateDirectory(playoutFolder);
            DateTimeOffset finish = result.Playout.Items[^1].Finish;
            string playoutPath = Path.Combine(playoutFolder, PlayoutJson.FileName(startAt, finish));
            await WriteAtomicAsync(playoutPath, PlayoutJson.Serialize(result.Playout), ct);
            Console.WriteLine($"\nWrote playout: {playoutPath}");

            if (xmltvFolder is not null)
            {
                Directory.CreateDirectory(xmltvFolder);
                string xmltvPath = Path.Combine(xmltvFolder, XmltvWriter.FileName(tvg!));
                await WriteAtomicAsync(xmltvPath, XmltvWriter.Write(tvg!, [result.Programme]), ct);
                Console.WriteLine($"Wrote guide:   {xmltvPath}");
            }
            return 0;
        });
        return cmd;
    }

    private static void PrintPlan(SpikeResult r, int clipCount)
    {
        Console.WriteLine($"Title:    {r.Programme.Title}");
        Console.WriteLine($"Slot:     {r.Programme.Start:HH:mm:ss} - {r.Programme.Stop:HH:mm:ss}  (pad {ProbeCommand.Fmt(r.PadMs)}, {clipCount} clips available)");
        Console.WriteLine($"Acts:     {r.Acts.Count}");
        foreach (Act a in r.Acts)
            Console.WriteLine($"  {ProbeCommand.Fmt(a.InMs)} - {ProbeCommand.Fmt(a.OutMs)}  ({ProbeCommand.Fmt(a.DurationMs)})");
        Console.WriteLine("Items:");
        foreach (PlayoutItem i in r.Playout.Items)
        {
            string what = i.Source switch
            {
                LocalSource l when l.InPointMs is not null => $"{Path.GetFileName(l.Path)} [{ProbeCommand.Fmt(l.InPointMs.Value)}-{ProbeCommand.Fmt(l.OutPointMs ?? 0)}]",
                LocalSource l => Path.GetFileName(l.Path),
                _ => "slate",
            };
            Console.WriteLine($"  {i.Start:HH:mm:ss.fff}  {i.Id,-12} {what}");
        }
        foreach (string w in r.Warnings)
            Console.WriteLine($"WARNING: {w}");
    }

    private static (string PlayoutFolder, string? FfprobePath) ReadChannelConfig(string channelJsonPath, string channelDir)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(channelJsonPath));
        string folder = "./playout";
        if (doc.RootElement.TryGetProperty("playout", out JsonElement playout) &&
            playout.TryGetProperty("folder", out JsonElement f) && f.GetString() is { Length: > 0 } s)
            folder = s;

        string? ffprobePath = null;
        if (doc.RootElement.TryGetProperty("ffmpeg", out JsonElement ffmpeg) &&
            ffmpeg.TryGetProperty("ffprobe_path", out JsonElement fp) && fp.GetString() is { Length: > 0 } fps)
            ffprobePath = fps;

        string resolved = Path.IsPathRooted(folder) ? folder : Path.GetFullPath(Path.Combine(channelDir, folder));
        return (resolved, ffprobePath);
    }

    private static DateTimeOffset ParseStart(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) return DateTimeOffset.Parse(value);
        DateTimeOffset now = DateTimeOffset.Now;
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).AddMinutes(1);
    }

    private static PaddingMode ParsePad(string value) => value.ToLowerInvariant() switch
    {
        "30" => PaddingMode.HalfHour,
        "60" => PaddingMode.Hour,
        "5" => PaddingMode.Next5,
        "10" => PaddingMode.Next10,
        "none" or "0" => PaddingMode.None,
        _ => throw new ArgumentException($"Unknown --pad value '{value}'. Use 30, 60, 5, 10, or none."),
    };

    /// <summary>Write to a temp name, then rename, so Next never reads a half-written file.</summary>
    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        string tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, path, overwrite: true);
    }
}
