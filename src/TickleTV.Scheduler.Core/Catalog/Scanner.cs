using System.Security.Cryptography;
using TickleTV.Scheduler.Core.Media;

namespace TickleTV.Scheduler.Core.Catalog;

public interface IMediaProber
{
    Task<MediaProbe> ProbeAsync(string path, CancellationToken ct = default);
}

public sealed record ScanProgress(int Done, int Total, string CurrentFile);

public sealed record ScanReport
{
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Moved { get; init; }
    public int Unchanged { get; init; }
    public int Missing { get; init; }
    /// <summary>Files whose facts came from a .nfo sidecar during this scan.</summary>
    public int WithSidecar { get; init; }
    public IReadOnlyList<string> Unclassified { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    /// <summary>Media roots that could not be opened this time (a network share that is off, an unplugged drive). Their files were left exactly as they were, not marked missing.</summary>
    public IReadOnlyList<string> UnreachableRoots { get; init; } = [];
}

/// <summary>
/// Walks media roots and keeps the catalog in step with the disk. A file is probed once; it is re-probed only
/// when its size or modified time changes. A file that moved keeps its identity, tags, and plays via its content hash.
/// Facts come from a .nfo sidecar when there is one (see <see cref="NfoSidecar"/>), else from the file name.
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

    public async Task<ScanReport> ScanAsync(IReadOnlyList<string> roots, CancellationToken ct = default, IProgress<ScanProgress>? progress = null)
    {
        int added = 0, updated = 0, moved = 0, unchanged = 0, withSidecar = 0;
        var unclassified = new List<string>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Enumerate first so progress can show "x of y". A root that cannot be opened (a share that is off) is
        // skipped entirely: its files are neither probed nor marked missing, so one bad night does not empty the channel.
        var files = new List<string>();
        var reachable = new List<string>();
        var unreachable = new List<string>();
        foreach (string root in roots)
        {
            string fullRoot = Path.GetFullPath(root);
            if (!Directory.Exists(fullRoot))
            {
                unreachable.Add(fullRoot);
                errors.Add($"Couldn't open the folder {fullRoot}. If it is on another computer or an external drive, check that it is on and connected. Its files were left as they were.");
                continue;
            }
            reachable.Add(fullRoot);
            files.AddRange(Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories)
                .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())));
        }
        progress?.Report(new ScanProgress(0, files.Count, ""));

        int done = 0;
        {
            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();
                seen.Add(file);
                progress?.Report(new ScanProgress(++done, files.Count, file));

                try
                {
                    var info = new FileInfo(file);
                    var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    MediaItem? byPath = _db.FindByPath(file);

                    if (byPath is not null && byPath.FileSize == info.Length && SameSecond(byPath.ModifiedUtc, modified) && byPath.MissingUtc is null)
                    {
                        // The video is as before, but a sidecar written since the last scan carries new facts.
                        if (NfoSidecar.ChangedSince(file, byPath.ProbedUtc))
                        {
                            RefreshFacts(byPath, now);
                            withSidecar++;
                            updated++;
                            _log?.Invoke($"sidecar  {file}");
                        }
                        else unchanged++;
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
                    ParsedName parsed = ParseFacts(file);
                    if (HasSidecarFacts(parsed)) withSidecar++;
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
                            Plot = parsed.Plot,
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
        foreach (MediaItem item in _db.ListUnderRoots(reachable))
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
            Added = added, Updated = updated, Moved = moved, Unchanged = unchanged, Missing = missing, WithSidecar = withSidecar,
            Unclassified = unclassified, Errors = errors, UnreachableRoots = unreachable,
        };
    }

    /// <summary>
    /// Re-reads sidecars and re-runs the name parser over every catalogued file without probing, refreshing
    /// kind, title, year, episode facts, and automatic tags. Anything a person set by hand is left alone.
    /// </summary>
    public int Retag()
    {
        int changed = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (MediaItem item in _db.ListItems(includeMissing: false))
        {
            RefreshFacts(item, now);
            changed++;
        }
        return changed;
    }

    /// <summary>The file-name guess with any .nfo sidecar facts laid over it.</summary>
    public static ParsedName ParseFacts(string path) => NfoSidecar.Enrich(FilenameParser.Parse(path), path);

    private static bool HasSidecarFacts(ParsedName parsed) => parsed.KindSource == FactSource.Nfo || parsed.ShowSource == FactSource.Nfo;

    /// <summary>Re-derives an item's facts from its sidecar and name, keeping manual answers, without probing the video.</summary>
    private void RefreshFacts(MediaItem item, DateTimeOffset now)
    {
        ParsedName parsed = ParseFacts(item.Path);
        if (item.KindSource != FactSource.Manual)
            _db.UpdateParsedFacts(item.Id, parsed.Kind, parsed.KindSource, parsed.Title, parsed.Year);
        if (parsed.Show is not null)
        {
            _db.UpsertEpisode(item.Id, new EpisodeInfo
            {
                Show = parsed.Show, Season = parsed.Season, Episode = parsed.Episode, AirDate = parsed.AirDate,
                EpisodeTitle = parsed.EpisodeTitle, Plot = parsed.Plot, Source = parsed.ShowSource ?? FactSource.Filename,
            });
        }
        ApplyAutoTags(item.Id, parsed, _db.FindById(item.Id)!);
        _db.MarkFactsRefreshed(item.Id, now);
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
        ApplyHolidayTags(id, parsed, item);
    }

    /// <summary>
    /// Holiday episodes, films, commercials and bumpers from the title, the sidecar plot, or (episodes only) the
    /// air date. A person's answer (a manual holiday label, or not-holiday) is left alone and switches the
    /// guessing off for that file.
    /// </summary>
    private void ApplyHolidayTags(long id, ParsedName parsed, MediaItem item)
    {
        if (item.Kind is not (MediaKind.Episode or MediaKind.Movie or MediaKind.Commercial or MediaKind.Bumper)) return;
        IReadOnlyList<Tag> manual = _db.GetTags(id);
        if (manual.Any(t => t.Source == FactSource.Manual && (t.Name == HolidayTagger.NotHoliday || t.Name.StartsWith(HolidayTagger.Prefix)))) return;

        // A person's episode facts beat the parsed ones, title included.
        EpisodeInfo? episode = _db.GetEpisode(id);
        string? title = item.Kind == MediaKind.Episode ? (episode?.EpisodeTitle ?? parsed.EpisodeTitle) : item.Title;
        string titleSource = episode?.Source == FactSource.Manual ? FactSource.Manual : (parsed.KindSource == FactSource.Nfo ? FactSource.Nfo : FactSource.Filename);
        foreach (HolidayHit hit in HolidayTagger.Hits(title, titleSource, episode?.Plot ?? parsed.Plot, episode?.AirDate ?? parsed.AirDate))
            _db.AddTag(id, hit.Tag, hit.Source == FactSource.Manual ? FactSource.Filename : hit.Source);
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
