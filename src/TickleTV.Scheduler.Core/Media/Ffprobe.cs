using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace TickleTV.Scheduler.Core.Media;

public sealed record Chapter(long StartMs, long EndMs, string? Title);

public sealed record MediaStreamInfo(int Index, string CodecType, string? CodecName);

public sealed record MediaProbe
{
    public required string Path { get; init; }
    public required long DurationMs { get; init; }
    public required IReadOnlyList<Chapter> Chapters { get; init; }
    public required IReadOnlyList<MediaStreamInfo> Streams { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
}

/// <summary>Runs ffprobe and extracts the fields the scheduler needs: duration, chapters, streams.</summary>
public sealed class Ffprobe : Catalog.IMediaProber
{
    private readonly string _ffprobePath;

    public Ffprobe(string? ffprobePath = null)
    {
        _ffprobePath = string.IsNullOrWhiteSpace(ffprobePath) ? "ffprobe" : ffprobePath;
    }

    public async Task<MediaProbe> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Media file not found.", path);

        var psi = new ProcessStartInfo
        {
            FileName = _ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[]
                 {
                     "-v", "error", "-print_format", "json",
                     "-show_format", "-show_streams", "-show_chapters", path,
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {_ffprobePath}.");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe exited {process.ExitCode}: {(await stderr).Trim()}");

        return Parse(path, await stdout);
    }

    public static MediaProbe Parse(string path, string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        long durationMs = 0;
        if (root.TryGetProperty("format", out JsonElement format) &&
            format.TryGetProperty("duration", out JsonElement duration))
        {
            durationMs = SecondsToMs(duration.GetString());
        }

        var chapters = new List<Chapter>();
        if (root.TryGetProperty("chapters", out JsonElement chapterArray))
        {
            foreach (JsonElement c in chapterArray.EnumerateArray())
            {
                long startMs = SecondsToMs(c.GetProperty("start_time").GetString());
                long endMs = SecondsToMs(c.GetProperty("end_time").GetString());
                string? title = null;
                if (c.TryGetProperty("tags", out JsonElement tags) &&
                    tags.TryGetProperty("title", out JsonElement t))
                {
                    title = t.GetString();
                }
                chapters.Add(new Chapter(startMs, endMs, title));
            }
        }

        var streams = new List<MediaStreamInfo>();
        int? width = null, height = null;
        if (root.TryGetProperty("streams", out JsonElement streamArray))
        {
            foreach (JsonElement s in streamArray.EnumerateArray())
            {
                int index = s.GetProperty("index").GetInt32();
                string codecType = s.GetProperty("codec_type").GetString() ?? "";
                string? codecName = s.TryGetProperty("codec_name", out JsonElement cn) ? cn.GetString() : null;
                streams.Add(new MediaStreamInfo(index, codecType, codecName));
                if (codecType == "video" && width is null)
                {
                    if (s.TryGetProperty("width", out JsonElement w)) width = w.GetInt32();
                    if (s.TryGetProperty("height", out JsonElement h)) height = h.GetInt32();
                }
            }
        }

        // Some containers report no format duration; fall back to the last chapter end.
        if (durationMs == 0 && chapters.Count > 0)
            durationMs = chapters[^1].EndMs;

        return new MediaProbe
        {
            Path = path,
            DurationMs = durationMs,
            Chapters = chapters.OrderBy(c => c.StartMs).ToList(),
            Streams = streams,
            Width = width,
            Height = height,
        };
    }

    private static long SecondsToMs(string? seconds)
    {
        if (string.IsNullOrWhiteSpace(seconds)) return 0;
        double s = double.Parse(seconds, CultureInfo.InvariantCulture);
        return (long)Math.Round(s * 1000.0);
    }
}
