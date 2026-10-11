using System.Text.Json;
using System.Text.Json.Serialization;

namespace TickleTV.Scheduler.Core.Playout;

/// <summary>
/// C# model of ErsatzTV Next's playout JSON (schema version 0.0.6).
/// Field names follow the schema exactly once serialized with <see cref="PlayoutJson.Options"/>.
/// See https://github.com/ErsatzTV/next/blob/main/schema/playout.json
/// </summary>
public sealed record Playout
{
    public const string SchemaVersion = "https://ersatztv.org/playout/version/0.0.6";

    public string Version { get; init; } = SchemaVersion;
    public required IReadOnlyList<PlayoutItem> Items { get; init; }
}

public sealed record PlayoutItem
{
    public required string Id { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset Finish { get; init; }
    public PlayoutItemSource? Source { get; init; }
    public PlayoutItemTracks? Tracks { get; init; }
    public GraphicsLayer? Watermark { get; init; }
    public IReadOnlyList<GraphicsLayer>? Graphics { get; init; }
}

public sealed record PlayoutItemTracks
{
    public TrackSelection? Video { get; init; }
    public TrackSelection? Audio { get; init; }
    public TrackSelection? Subtitle { get; init; }
}

public sealed record TrackSelection
{
    public PlayoutItemSource? Source { get; init; }
    public int? StreamIndex { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "source_type")]
[JsonDerivedType(typeof(LocalSource), "local")]
[JsonDerivedType(typeof(LavfiSource), "lavfi")]
[JsonDerivedType(typeof(HttpSource), "http")]
public abstract record PlayoutItemSource;

/// <summary>A file on the filesystem reachable by the Next server.</summary>
public sealed record LocalSource : PlayoutItemSource
{
    public required string Path { get; init; }
    public long? InPointMs { get; init; }
    public long? OutPointMs { get; init; }
}

/// <summary>A synthetic source produced by an ffmpeg lavfi filter graph.</summary>
public sealed record LavfiSource : PlayoutItemSource
{
    public required string Params { get; init; }
}

/// <summary>A remote source fetched over HTTP(S). URI and headers may contain {{ENV_VAR}} placeholders.</summary>
public sealed record HttpSource : PlayoutItemSource
{
    public required string Uri { get; init; }
    public bool? IsLive { get; init; }
    public long? InPointMs { get; init; }
    public long? OutPointMs { get; init; }
    public IReadOnlyList<string>? Headers { get; init; }
    public string? UserAgent { get; init; }
}

public sealed record GraphicsLayer
{
    public required PlayoutItemSource Source { get; init; }
    public int? StreamIndex { get; init; }
    public required string Location { get; init; }
    public double? WidthPercent { get; init; }
    public double? HorizontalMarginPercent { get; init; }
    public double? VerticalMarginPercent { get; init; }
    public double? OpacityPercent { get; init; }
    public bool? WithinSourceContent { get; init; }
}

public static class PlayoutJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string Serialize(Playout playout) => JsonSerializer.Serialize(playout, Options);

    public static Playout Deserialize(string json) =>
        JsonSerializer.Deserialize<Playout>(json, Options)
        ?? throw new InvalidDataException("Playout JSON deserialized to null.");

    /// <summary>
    /// Next locates the playout for the current time by file name, so the name must be
    /// <c>{start}_{finish}.json</c> in compact ISO 8601 with nanosecond precision,
    /// for example <c>20260413T000000.000000000-0500_20260414T002131.620000000-0500.json</c>.
    /// </summary>
    public static string FileName(DateTimeOffset start, DateTimeOffset finish) =>
        $"{Compact(start)}_{Compact(finish)}.json";

    private static string Compact(DateTimeOffset t)
    {
        // .NET keeps 7 fractional digits (ticks); the schema example shows 9, so pad with "00".
        string fraction = t.ToString("fffffff") + "00";
        string offset = t.ToString("zzz").Replace(":", "");
        return $"{t:yyyyMMdd'T'HHmmss}.{fraction}{offset}";
    }
}
