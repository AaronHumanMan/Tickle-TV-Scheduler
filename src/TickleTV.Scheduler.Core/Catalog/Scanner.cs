using System.Security.Cryptography;
using TickleTV.Scheduler.Core.Media;

namespace TickleTV.Scheduler.Core.Catalog;

public interface IMediaProber
{
    Task<MediaProbe> ProbeAsync(string path, CancellationToken ct = default);
}

public sealed record ScanReport
{
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Moved { get; init; }
    public int Unchanged { get; init; }
    public int Missing { get; init; }
    public IReadOnlyList<string> Unclassified { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>
/// Walks media roots and keeps the catalog in step with the disk. A file is probed once; it is re-probed only
/// when its size or modified time changes. A file that moved keeps its identity, tags, and plays via its content hash.
/// </summary>
public sealed class Scanner
{
    private static readonly string[] Extensions = [".mkv", ".mp4", ".m4v", ".mov", ".avi", ".ts", ".webm", ".mpg", ".mpeg", ".wmv"];

    private readonly CatalogDb _db;
    private readonly IMediaProber _prober;
    private readonly Action<string>? _log;

    public Scanner(CatalogDb db, IMediaProber prober, Action<string>? log = null)
    {
        _db = db;
        _prober = prober;
        _log = log;
    }

    public async Task<ScanReport> ScanAsync(IReadOnlyList<string> roots, CancellationToken ct = default)
    {
        int added = 0, updated = 0, moved = 0, unchanged = 0;
        var unclassified = new List<string>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (string root in roots)
        {
            string fullRoot = Path.GetFullPath(root);
            if (!Directory.Exists(fullRoot))
            {
                errors.Add($"Root not found: {fullRoot}");
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (!Extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                seen.Add(file);

                try
                {
                    var info = new FileInfo(file);
                    var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    MediaItem? byPath = _db.FindByPath(file);

                    if (byPath is not null && byPath.FileSize == info.Length && SameSecond(byPath.ModifiedUtc, modified) && byPath.MissingUtc is null)
                    {
                        unchanged++;
                        continue;
                    }

                    string hash = ContentHash(file, info.Length);
                    MediaItem? byHash = byPath is null ? _db.FindByHash(hash) : null;

                    if (byHash is not null)
                    {
                        // Same bytes at a new path: keep identity, tags, and history.
                        _db.UpdatePath(byHash.Id, file, info.Length, modified);
                        _log?.Invoke($"moved    {byHash.Path} -> {file}");
                        moved++;
                        continue;
                    }

                    MediaProbe probe = await _prober.ProbeAsync(file, ct);
                    ParsedName parsed = FilenameParser.Parse(file);
                    MediaItem fresh = ToItem(file, info.Length, modified, hash, probe, parsed, now);

                    long id;
                    if (byPath is null)
                    {
                        id = _db.Insert(fresh);
                        added++;
                        _log?.Invoke($"added    {Describe(fresh, parsed)}");
                    }
                    else
                    {
                        id = byPath.Id;
                        _db.UpdateProbe(id, fresh);
                        updated++;
                        _log?.Invoke($"updated  {Describe(fresh, parsed)}");
                    }

                    _db.ReplaceChapters(id, probe.Chapters.Select((c, i) => new CatalogChapter(i, c.StartMs, c.EndMs, c.Title)));
                    if (parsed.Show is not null)
                    {
                        _db.UpsertEpisode(id, new EpisodeInfo
                        {
                            Show = parsed.Show,
                            Season = parsed.Season,
                            Episode = parsed.Episode,
                            AirDate = parsed.AirDate,
                            EpisodeTitle = parsed.EpisodeTitle,
                            Source = parsed.ShowSource ?? FactSource.Filename,
                        });
                    }
                    ApplyAutoTags(id, parsed, _db.FindById(id)!);

                    if (_db.FindById(id)!.Kind == MediaKind.Unknown) unclassified.Add(file);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{file}: {ex.Message}");
                    _log?.Invoke($"error    {file}: {ex.Message}");
                }
            }
        }

        int missing = 0;
        foreach (MediaItem item in _db.ListUnderRoots(roots))
        {
            if (seen.Contains(item.Path) || File.Exists(item.Path)) continue;
            if (item.MissingUtc is null)
            {
                _db.MarkMissing(item.Id, now);
                _log?.Invoke($"missing  {item.Path}");
            }
            missing++;
        }

        return new ScanReport
        {
            Added = added, Updated = updated, Moved = moved, Unchanged = unchanged, Missing = missing,
            Unclassified = unclassified, Errors = errors,
        };
    }

    /// <summary>
    /// Re-runs the name parser over every catalogued file without probing, refreshing kind, title, year,
    /// episode facts, and automatic tags. Anything a person set by hand is left alone.
    /// </summary>
    public int Retag()
    {
        int changed = 0;
        foreach (MediaItem item in _db.ListItems(includeMissing: false))
        {
            ParsedName parsed = FilenameParser.Parse(item.Path);
            if (item.KindSource != FactSource.Manual)
            {
                _db.UpdateParsedFacts(item.Id, parsed.Kind, parsed.KindSource, parsed.Title, parsed.Year);
            }
            if (parsed.Show is not null)
            {
                _db.UpsertEpisode(item.Id, new EpisodeInfo
                {
                    Show = parsed.Show, Season = parsed.Season, Episode = parsed.Episode, AirDate = parsed.AirDate,
                    EpisodeTitle = parsed.EpisodeTitle, Source = parsed.ShowSource ?? FactSource.Filename,
                });
            }
            ApplyAutoTags(item.Id, parsed, _db.FindById(item.Id)!);
            changed++;
        }
        return changed;
    }

    private void ApplyAutoTags(long id, ParsedName parsed, MediaItem item)
    {
        _db.ClearAutoTags(id);
        _db.AddTag(id, item.Kind.ToString(), FactSource.Filename);
        if (item.Year is { } y)
        {
            _db.AddTag(id, y.ToString(), FactSource.Filename);
            _db.AddTag(id, $"{y / 10 * 10}s", FactSource.Filename);
        }
        if (parsed.Show is not null) _db.AddTag(id, $"show:{parsed.Show}", parsed.ShowSource ?? FactSource.Filename);
        foreach (string tag in parsed.Tags) _db.AddTag(id, tag, FactSource.Filename);
    }

    private static MediaItem ToItem(string path, long size, DateTimeOffset modified, string hash, MediaProbe probe, ParsedName parsed, DateTimeOffset now)
    {
        MediaStreamInfo? video = probe.Streams.FirstOrDefault(s => s.CodecType == "video");
        MediaStreamInfo? audio = probe.Streams.FirstOrDefault(s => s.CodecType == "audio");
        return new MediaItem
        {
            ContentHash = hash,
            Path = path,
            FileSize = size,
            ModifiedUtc = modified,
            Kind = parsed.Kind,
            KindSource = parsed.KindSource,
            Title = parsed.Title,
            Year = parsed.Year,
            DurationMs = probe.DurationMs,
            VideoCodec = video?.CodecName,
            AudioCodec = audio?.CodecName,
            Width = probe.Width,
            Height = probe.Height,
            ProbedUtc = now,
        };
    }

    private static string Describe(MediaItem item, ParsedName parsed) =>
        parsed.Show is null
            ? $"[{item.Kind}] {item.Title}{(item.Year is null ? "" : $" ({item.Year})")}"
            : $"[{item.Kind}] {parsed.Show} S{parsed.Season:00}E{parsed.Episode:00}{(parsed.EpisodeTitle is null ? "" : " " + parsed.EpisodeTitle)}";

    private static bool SameSecond(DateTimeOffset a, DateTimeOffset b) =>
        Math.Abs((a - b).TotalSeconds) < 1;

    /// <summary>SHA-256 over the first and last megabyte plus the size: fast, and stable across renames and moves.</summary>
    public static string ContentHash(string path, long size)
    {
        const int window = 1024 * 1024;
        using var sha = SHA256.Create();
        using FileStream fs = File.OpenRead(path);
        var buffer = new byte[window];

        int read = fs.Read(buffer, 0, window);
        sha.TransformBlock(buffer, 0, read, null, 0);

        if (size > window)
        {
            fs.Seek(-Math.Min(window, size), SeekOrigin.End);
            read = fs.Read(buffer, 0, window);
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        byte[] sizeBytes = BitConverter.GetBytes(size);
        sha.TransformFinalBlock(sizeBytes, 0, sizeBytes.Length);
        return Convert.ToHexStringLower(sha.Hash!);
    }
}
