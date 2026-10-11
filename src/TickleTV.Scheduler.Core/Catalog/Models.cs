namespace TickleTV.Scheduler.Core.Catalog;

public enum MediaKind
{
    Unknown,
    Episode,
    Movie,
    Commercial,
    Bumper,
    Filler,
    /// <summary>Recognised but not airable as anything: credits, titles, clips marked "not an advert".</summary>
    Other,
}

/// <summary>Who decided a classification: a person always wins over a rule.</summary>
public static class FactSource
{
    public const string Manual = "manual";
    public const string Filename = "filename";
    public const string Folder = "folder";
    public const string Default = "default";
}

public sealed record MediaItem
{
    public long Id { get; init; }
    public required string ContentHash { get; init; }
    public required string Path { get; init; }
    public required long FileSize { get; init; }
    public required DateTimeOffset ModifiedUtc { get; init; }
    public required MediaKind Kind { get; init; }
    public required string KindSource { get; init; }
    public string? Title { get; init; }
    public int? Year { get; init; }
    public required long DurationMs { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public DateTimeOffset ProbedUtc { get; init; }
    public DateTimeOffset? MissingUtc { get; init; }
}

public sealed record EpisodeInfo
{
    public required string Show { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public DateOnly? AirDate { get; init; }
    public string? EpisodeTitle { get; init; }
    public required string Source { get; init; }
}

public sealed record CatalogChapter(int Ordinal, long StartMs, long EndMs, string? Title);

public sealed record Tag(string Name, string Source);

/// <summary>A media item with its related rows, as the engine consumes it.</summary>
public sealed record CatalogEntry
{
    public required MediaItem Item { get; init; }
    public EpisodeInfo? Episode { get; init; }
    public required IReadOnlyList<CatalogChapter> Chapters { get; init; }
    public required IReadOnlyList<Tag> Tags { get; init; }
}

/// <summary>Why an item is in the review queue.</summary>
public enum AttentionReason
{
    /// <summary>The scan could not tell what kind of file this is.</summary>
    Unknown,
    /// <summary>A TV episode with no show name, season, or episode number.</summary>
    EpisodeDetails,
    /// <summary>A commercial with no brand label.</summary>
    NoBrand,
    /// <summary>A commercial with no category label.</summary>
    NoCategory,
    /// <summary>A bumper that is not marked as a promo, a station ID, or a break bumper.</summary>
    BumperUnsorted,
    /// <summary>An episode with no chapter marks, so breaks fall back to even spacing. Low priority; its own filter.</summary>
    NoChapters,
}
