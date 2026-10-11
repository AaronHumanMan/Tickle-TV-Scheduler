using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TickleTV.Scheduler.Core.Catalog;

/// <summary>What a Kodi-style .nfo file says about a video, before it is merged with the file-name guess.</summary>
public sealed record NfoFacts
{
    /// <summary>Episode, Movie, or Unknown when the file only names the show.</summary>
    public required MediaKind Kind { get; init; }
    public string? Title { get; init; }
    public string? Show { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public DateOnly? AirDate { get; init; }
    public int? Year { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
}

/// <summary>
/// Reads the metadata sidecars that Kodi, Jellyfin, Emby, tinyMediaManager and similar tools leave next to
/// videos: <c>Episode.nfo</c> beside <c>Episode.mkv</c> with an <c>&lt;episodedetails&gt;</c> root, a
/// <c>&lt;movie&gt;</c> root for films, and <c>tvshow.nfo</c> in the show folder naming the show. Facts from a
/// sidecar beat guesses from the file name, because a person or a metadata lookup wrote them on purpose. Files
/// that are not XML (some tools leave a bare web address) are ignored.
/// </summary>
public static partial class NfoSidecar
{
    [GeneratedRegex(@"<\?xml[^>]*\?>", RegexOptions.IgnoreCase)]
    private static partial Regex XmlDeclaration();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlug();

    /// <summary>The sidecar that belongs to this video, whether or not it exists.</summary>
    public static string PathFor(string videoPath) => Path.ChangeExtension(videoPath, ".nfo");

    /// <summary>True when the video has a sidecar written after <paramref name="since"/>, so its facts should be read again.</summary>
    public static bool ChangedSince(string videoPath, DateTimeOffset since)
    {
        var nfo = new FileInfo(PathFor(videoPath));
        return nfo.Exists && new DateTimeOffset(nfo.LastWriteTimeUtc, TimeSpan.Zero) > since;
    }

    /// <summary>Reads the video's own sidecar plus a tvshow.nfo in its folder or the one above; null when there is neither.</summary>
    public static NfoFacts? Read(string videoPath)
    {
        NfoFacts? own = ReadFile(PathFor(videoPath));
        NfoFacts? show = ReadShowFile(videoPath);
        if (own is null && show is null) return null;
        if (own is null) return show;
        return own with
        {
            Show = own.Show ?? show?.Show,
            Genres = own.Genres.Count > 0 ? own.Genres : show?.Genres ?? [],
        };
    }

    /// <summary>Merges sidecar facts over the file-name guess. Anything the sidecar says wins; gaps keep the guess.</summary>
    public static ParsedName Enrich(ParsedName parsed, string videoPath)
    {
        NfoFacts? nfo = Read(videoPath);
        if (nfo is null) return parsed;

        var tags = parsed.Tags.ToList();
        foreach (string genre in nfo.Genres)
        {
            string slug = Slug(genre);
            if (slug.Length > 0) tags.Add("genre:" + slug);
        }

        if (nfo.Kind == MediaKind.Episode || (nfo.Kind == MediaKind.Unknown && parsed.Kind == MediaKind.Episode))
        {
            string? show = nfo.Show ?? parsed.Show;
            int? season = nfo.Season ?? parsed.Season;
            int? episode = nfo.Episode ?? parsed.Episode;
            string? episodeTitle = nfo.Title ?? parsed.EpisodeTitle;
            DateOnly? airDate = nfo.AirDate ?? parsed.AirDate;
            int? year = airDate?.Year ?? nfo.Year ?? parsed.Year;
            tags = RefreshDecade(tags, year);
            string number = season is { } s && episode is { } e ? $" S{s:00}E{e:00}" : episode is { } e2 ? $" E{e2:00}" : "";
            return parsed with
            {
                Kind = MediaKind.Episode,
                KindSource = nfo.Kind == MediaKind.Episode ? FactSource.Nfo : parsed.KindSource,
                Title = show is null ? (episodeTitle ?? parsed.Title) : $"{show}{number}" + (episodeTitle is null ? "" : $" {episodeTitle}"),
                Year = year,
                Show = show,
                ShowSource = nfo.Show is not null ? FactSource.Nfo : parsed.ShowSource,
                Season = season,
                Episode = episode,
                AirDate = airDate,
                EpisodeTitle = episodeTitle,
                Tags = tags.Distinct().ToList(),
            };
        }

        if (nfo.Kind == MediaKind.Movie)
        {
            int? year = nfo.Year ?? nfo.AirDate?.Year ?? parsed.Year;
            tags = RefreshDecade(tags, year);
            return parsed with
            {
                Kind = MediaKind.Movie,
                KindSource = FactSource.Nfo,
                Title = nfo.Title ?? parsed.Title,
                Year = year,
                Show = null,
                ShowSource = null,
                Season = null,
                Episode = null,
                AirDate = nfo.AirDate ?? parsed.AirDate,
                EpisodeTitle = null,
                Tags = tags.Distinct().ToList(),
            };
        }

        // Only a tvshow.nfo nearby: the file name did not look like an episode, so leave the kind alone but keep the genres.
        return parsed with { Tags = tags.Distinct().ToList() };
    }

    /// <summary>Parses one .nfo file. Null when it is missing, not XML, or has no root we understand.</summary>
    public static NfoFacts? ReadFile(string nfoPath)
    {
        if (!File.Exists(nfoPath)) return null;
        string text;
        try { text = File.ReadAllText(nfoPath); }
        catch (IOException) { return null; }
        return Parse(text);
    }

    /// <summary>Parses .nfo text. Kodi writes several &lt;episodedetails&gt; roots for a multi-episode file; the first one is used.</summary>
    public static NfoFacts? Parse(string text)
    {
        int start = text.IndexOf('<');
        if (start < 0) return null;
        string body = XmlDeclaration().Replace(text[start..], "").Trim();
        if (body.Length == 0) return null;

        XDocument doc;
        try { doc = XDocument.Parse("<nfo>" + body + "</nfo>"); }
        catch (System.Xml.XmlException) { return null; }

        XElement? root = doc.Root!.Elements().FirstOrDefault(e => Local(e) is "episodedetails" or "movie" or "tvshow");
        if (root is null) return null;

        string kind = Local(root);
        List<string> genres = root.Elements().Where(e => Local(e) == "genre").Select(e => e.Value.Trim()).Where(g => g.Length > 0).ToList();
        DateOnly? aired = Date(Text(root, "aired")) ?? Date(Text(root, "premiered"));
        int? year = Int(Text(root, "year")) ?? aired?.Year;

        return kind switch
        {
            "episodedetails" => new NfoFacts
            {
                Kind = MediaKind.Episode,
                Title = Text(root, "title"),
                Show = Text(root, "showtitle"),
                Season = NonNegative(Int(Text(root, "season"))),
                Episode = NonNegative(Int(Text(root, "episode"))),
                AirDate = aired,
                Year = year,
                Genres = genres,
            },
            "movie" => new NfoFacts
            {
                Kind = MediaKind.Movie,
                Title = Text(root, "title"),
                AirDate = aired,
                Year = year,
                Genres = genres,
            },
            _ => new NfoFacts
            {
                Kind = MediaKind.Unknown,
                Show = Text(root, "title") ?? Text(root, "showtitle"),
                Year = year,
                Genres = genres,
            },
        };
    }

    /// <summary>tvshow.nfo beside the video or one folder up (the usual Show/Season 1/Episode.mkv layout).</summary>
    private static NfoFacts? ReadShowFile(string videoPath)
    {
        string? dir = Path.GetDirectoryName(videoPath);
        for (int depth = 0; depth < 2 && !string.IsNullOrEmpty(dir); depth++)
        {
            NfoFacts? facts = ReadFile(Path.Combine(dir, "tvshow.nfo"));
            if (facts is not null && facts.Kind == MediaKind.Unknown) return facts;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static List<string> RefreshDecade(List<string> tags, int? year)
    {
        if (year is not { } y) return tags;
        tags.RemoveAll(t => Regex.IsMatch(t, @"^(?:19|20)\d0s$"));
        tags.Add($"{y / 10 * 10}s");
        return tags;
    }

    private static string Local(XElement e) => e.Name.LocalName.ToLowerInvariant();

    private static string? Text(XElement root, string name)
    {
        string? value = root.Elements().FirstOrDefault(e => Local(e) == name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int? Int(string? s) =>
        s is not null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;

    /// <summary>Kodi uses -1 for "not known".</summary>
    private static int? NonNegative(int? n) => n is { } v && v >= 0 ? v : null;

    private static DateOnly? Date(string? s)
    {
        if (s is null) return null;
        string head = s.Length > 10 ? s[..10] : s;
        return DateOnly.TryParseExact(head, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d) ? d : null;
    }

    private static string Slug(string s) => NonSlug().Replace(s.ToLowerInvariant(), "-").Trim('-');
}
