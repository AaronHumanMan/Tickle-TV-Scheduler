using System.Text.Json;
using System.Text.Json.Nodes;

namespace TickleTV.Scheduler.Core.Config;

/// <summary>Plain-language choices that map onto Next's channel.json normalization settings.</summary>
public sealed record ChannelProfile
{
    /// <summary>480, 720, or 1080.</summary>
    public int Quality { get; init; } = 1080;
    /// <summary>h264 or hevc.</summary>
    public string Codec { get; init; } = "h264";
    /// <summary>none, nvidia, intel, amd, vaapi, videotoolbox, vulkan.</summary>
    public string Hardware { get; init; } = "none";
    /// <summary>Pass through files whose video already matches instead of re-encoding them.</summary>
    public bool PassThrough { get; init; } = false;
    /// <summary>Even out loudness between items.</summary>
    public bool NormalizeLoudness { get; init; } = true;

    public (int Width, int Height, int BitrateKbps) Video => Quality switch
    {
        480 => (854, 480, 1500),
        720 => (1280, 720, 3000),
        _ => (1920, 1080, 5000),
    };

    public string? Accel => Hardware.Trim().ToLowerInvariant() switch
    {
        "nvidia" or "cuda" => "cuda",
        "intel" or "qsv" => "qsv",
        "amd" or "amf" => "amf",
        "vaapi" => "vaapi",
        "videotoolbox" or "apple" => "videotoolbox",
        "vulkan" => "vulkan",
        "rkmpp" => "rkmpp",
        _ => null,
    };

    public static string HardwareFromAccel(string? accel) => accel switch
    {
        "cuda" => "nvidia",
        "qsv" => "intel",
        "amf" => "amd",
        null or "" => "none",
        _ => accel,
    };
}

/// <summary>One channel as Next sees it: its lineup entry plus the profile read from its channel.json.</summary>
public sealed record NextChannel
{
    public required string Number { get; init; }
    public required string Name { get; init; }
    public string? TvgId { get; init; }
    public string? Logo { get; init; }
    public string? Group { get; init; }
    public required string ConfigPath { get; init; }
    public required string PlayoutFolder { get; init; }
    public ChannelProfile Profile { get; init; } = new();
    public string Summary => $"{Profile.Quality}p {Profile.Codec.ToUpperInvariant()}, {(Profile.Accel is null ? "software" : "hardware: " + ChannelProfile.HardwareFromAccel(Profile.Accel))}{(Profile.PassThrough ? ", pass-through on" : "")}";
}

/// <summary>
/// Reads and edits Next's lineup.json and channel.json files. Unknown fields are kept as they are; only the
/// settings this editor owns are rewritten. Next reads these files at startup, so it must be restarted after a change.
/// </summary>
public static class NextLineup
{
    public const string ChannelSchemaVersion = "https://ersatztv.org/channel/version/0.1.2";

    public static IReadOnlyList<NextChannel> Load(string lineupPath)
    {
        if (!File.Exists(lineupPath)) return [];
        string lineupDir = Path.GetDirectoryName(lineupPath)!;
        JsonNode root = JsonNode.Parse(File.ReadAllText(lineupPath)) ?? new JsonObject();
        var result = new List<NextChannel>();
        foreach (JsonNode? c in root["channels"]?.AsArray() ?? [])
        {
            if (c is null) continue;
            string configPath = Resolve(lineupDir, c["config"]?.GetValue<string>() ?? "");
            (ChannelProfile profile, string playoutFolder) = ReadChannelFile(configPath);
            result.Add(new NextChannel
            {
                Number = c["number"]?.GetValue<string>() ?? "",
                Name = c["name"]?.GetValue<string>() ?? "",
                TvgId = c["tvg_id"]?.GetValue<string>(),
                Logo = c["logo"]?.GetValue<string>(),
                Group = c["group"]?.GetValue<string>(),
                ConfigPath = configPath,
                PlayoutFolder = playoutFolder,
                Profile = profile,
            });
        }
        return result;
    }

    /// <summary>Adds a channel: a lineup entry, a channel.json next to the others, and its playout folder.</summary>
    public static NextChannel AddChannel(string lineupPath, string number, string name, string? tvgId, ChannelProfile profile, string? logo = null, string? group = null)
    {
        string lineupDir = Path.GetDirectoryName(lineupPath)!;
        JsonObject root = (JsonNode.Parse(File.ReadAllText(lineupPath)) as JsonObject) ?? new JsonObject();
        JsonArray channels = root["channels"] as JsonArray ?? new JsonArray();
        root["channels"] = channels;
        if (channels.Any(c => c?["number"]?.GetValue<string>() == number))
            throw new InvalidOperationException($"Channel {number} already exists.");

        string relativeConfig = $"./channels/{number}/channel.json";
        string configPath = Resolve(lineupDir, relativeConfig);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(configPath)!, "playout"));

        // Start from a sibling channel's file so ffmpeg paths and anything else carry over.
        JsonObject channelJson = TemplateFrom(channels, lineupDir) ?? DefaultChannelJson();
        ApplyProfile(channelJson, profile);
        channelJson["version"] = ChannelSchemaVersion;
        channelJson["playout"] = new JsonObject { ["folder"] = "./playout" };
        WriteAtomic(configPath, channelJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var entry = new JsonObject
        {
            ["number"] = number,
            ["name"] = name,
            ["config"] = relativeConfig,
            ["tvg_id"] = string.IsNullOrWhiteSpace(tvgId) ? $"tickle.{number}" : tvgId,
        };
        if (!string.IsNullOrWhiteSpace(logo)) entry["logo"] = logo;
        if (!string.IsNullOrWhiteSpace(group)) entry["group"] = group;
        channels.Add(entry);
        WriteAtomic(lineupPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return Load(lineupPath).First(c => c.Number == number);
    }

    /// <summary>Rewrites the profile settings in an existing channel.json, and the name, logo, and group in the lineup.</summary>
    public static void UpdateChannel(string lineupPath, string number, string name, ChannelProfile profile, string? logo = null, string? group = null)
    {
        string lineupDir = Path.GetDirectoryName(lineupPath)!;
        JsonObject root = (JsonNode.Parse(File.ReadAllText(lineupPath)) as JsonObject) ?? throw new InvalidDataException("lineup.json is not an object.");
        JsonObject entry = root["channels"]?.AsArray().OfType<JsonObject>().FirstOrDefault(c => c["number"]?.GetValue<string>() == number)
            ?? throw new InvalidOperationException($"Channel {number} is not in the lineup.");

        entry["name"] = name;
        if (string.IsNullOrWhiteSpace(logo)) entry.Remove("logo"); else entry["logo"] = logo;
        if (string.IsNullOrWhiteSpace(group)) entry.Remove("group"); else entry["group"] = group;
        WriteAtomic(lineupPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        string configPath = Resolve(lineupDir, entry["config"]?.GetValue<string>() ?? "");
        JsonObject channelJson = (JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject) ?? DefaultChannelJson();
        ApplyProfile(channelJson, profile);
        WriteAtomic(configPath, channelJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string NextFreeNumber(IReadOnlyList<NextChannel> channels)
    {
        int n = 1;
        var taken = channels.Select(c => int.TryParse(c.Number, out int v) ? v : 0).ToHashSet();
        while (taken.Contains(n)) n++;
        return n.ToString();
    }

    private static (ChannelProfile Profile, string PlayoutFolder) ReadChannelFile(string configPath)
    {
        if (!File.Exists(configPath)) return (new ChannelProfile(), Path.Combine(Path.GetDirectoryName(configPath) ?? "", "playout"));
        JsonObject? c = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
        JsonObject? video = c?["normalization"]?["video"] as JsonObject;
        JsonObject? audio = c?["normalization"]?["audio"] as JsonObject;
        int height = video?["height"]?.GetValue<int>() ?? 1080;
        var profile = new ChannelProfile
        {
            Quality = height <= 480 ? 480 : height <= 720 ? 720 : 1080,
            Codec = video?["format"]?.GetValue<string>() ?? "h264",
            Hardware = ChannelProfile.HardwareFromAccel(video?["accel"]?.GetValue<string>()),
            PassThrough = (video?["mode"]?.GetValue<string>() ?? "transcode") == "copy",
            NormalizeLoudness = audio?["normalize_loudness"]?.GetValue<bool>() ?? false,
        };
        string folder = c?["playout"]?["folder"]?.GetValue<string>() ?? "./playout";
        return (profile, Resolve(Path.GetDirectoryName(configPath)!, folder));
    }

    private static void ApplyProfile(JsonObject channelJson, ChannelProfile profile)
    {
        JsonObject normalization = channelJson["normalization"] as JsonObject ?? new JsonObject();
        channelJson["normalization"] = normalization;
        JsonObject video = normalization["video"] as JsonObject ?? new JsonObject();
        normalization["video"] = video;
        JsonObject audio = normalization["audio"] as JsonObject ?? new JsonObject();
        normalization["audio"] = audio;

        (int w, int h, int kbps) = profile.Video;
        video["mode"] = profile.PassThrough ? "copy" : "transcode";
        video["format"] = profile.Codec;
        video["bit_depth"] = 8;
        video["width"] = w;
        video["height"] = h;
        video["bitrate_kbps"] = kbps;
        video["buffer_kbps"] = kbps * 2;
        video["accel"] = profile.Accel is null ? null : JsonValue.Create(profile.Accel);
        if (!video.ContainsKey("tonemap_algorithm")) video["tonemap_algorithm"] = "linear";

        audio["mode"] = "transcode";
        if (!audio.ContainsKey("format")) audio["format"] = "aac";
        if (!audio.ContainsKey("bitrate_kbps")) audio["bitrate_kbps"] = 192;
        if (!audio.ContainsKey("buffer_kbps")) audio["buffer_kbps"] = 384;
        if (!audio.ContainsKey("channels")) audio["channels"] = 2;
        if (!audio.ContainsKey("sample_rate_hz")) audio["sample_rate_hz"] = 48000;
        audio["normalize_loudness"] = profile.NormalizeLoudness;
        if (profile.NormalizeLoudness && !audio.ContainsKey("loudness"))
            audio["loudness"] = new JsonObject { ["integrated_target"] = -16, ["range_target"] = 11, ["true_peak"] = -1.5 };

        if (normalization["subtitle"] is null) normalization["subtitle"] = new JsonObject { ["mode"] = "burn" };
    }

    private static JsonObject? TemplateFrom(JsonArray channels, string lineupDir)
    {
        foreach (JsonNode? c in channels)
        {
            string path = Resolve(lineupDir, c?["config"]?.GetValue<string>() ?? "");
            if (!File.Exists(path)) continue;
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject o) return o;
        }
        return null;
    }

    private static JsonObject DefaultChannelJson() => new()
    {
        ["version"] = ChannelSchemaVersion,
        ["playout"] = new JsonObject { ["folder"] = "./playout" },
        ["ffmpeg"] = new JsonObject { ["ffmpeg_path"] = "", ["ffprobe_path"] = "", ["disabled_filters"] = new JsonArray() },
        ["normalization"] = new JsonObject
        {
            ["audio"] = new JsonObject(),
            ["video"] = new JsonObject(),
            ["subtitle"] = new JsonObject { ["mode"] = "burn" },
        },
    };

    private static string Resolve(string baseDir, string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDir, path));

    private static void WriteAtomic(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
