using System.Globalization;
using System.Text.RegularExpressions;

namespace TickleTV.Scheduler.Core.Catalog;

public sealed record ParsedName
{
    public required MediaKind Kind { get; init; }
    public required string KindSource { get; init; }
    public required string Title { get; init; }
    public int? Year { get; init; }
    public string? Show { get; init; }
    public string? ShowSource { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public DateOnly? AirDate { get; init; }
    public string? EpisodeTitle { get; init; }
    /// <summary>Tags derived from the name and folders, such as brand:ross-perot, psa, unbranded, 1990s.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>
/// Pulls show, season, episode, air date, year, kind, and tags out of a file name and its folders.
/// Heuristic by design: anything it gets wrong is fixed once with a manual tag, which wins thereafter.
/// </summary>
public static partial class FilenameParser
{
    // "(480p_30fps_H264-128kbit_AAC)" and similar technical suffixes.
    [GeneratedRegex(@"\s*[\(\[][^\)\]]*(?:\d{3,4}p|x26[45]|h\.?26[45]|hevc|aac|ac3|web-?dl|bluray|hdtv|kbit)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalParens();

    // Trailing duplicate markers from file managers: "name (2)".
    [GeneratedRegex(@"\s*\(\d+\)\s*$")]
    private static partial Regex DuplicateSuffix();

    [GeneratedRegex(@"\bS(?<s>\d{1,2})\s?[.\s]?E(?<e>\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisode();

    [GeneratedRegex(@"\b(?<s>\d{1,2})x(?<e>\d{2,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonXEpisode();

    [GeneratedRegex(@"\b(?<y>(?:19|20)\d{2})-(?<m>\d{2})-(?<d>\d{2})\b")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<![\d-])(?<y>(?:19|20)\d{2})(?![\d-])")]
    private static partial Regex Year();

    // "90s", "1990s", "80's"
    [GeneratedRegex(@"\b(?<c>19|20)?(?<d>[0-9])0'?s\b", RegexOptions.IgnoreCase)]
    private static partial Regex Decade();

    [GeneratedRegex(@"^(?:season|series)\s*\d+$|^s\d{1,2}$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"\b(commercials?|advert(?:isement)?s?|ads?|psa|promos?|spots?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CommercialWords();

    [GeneratedRegex(@"\b(bumpers?|idents?|station\s*id|interstitials?|stingers?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BumperWords();

    [GeneratedRegex(@"\b(fillers?|fallback|slate)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FillerWords();

    // Things that live among commercials but are not airable as one.
    [GeneratedRegex(@"\bnot\s+an?\s+(?:advert|ad|commercial)\b|\b(?:end|closing|opening)\s+(?:credits|titles)\b|\bend\s+card\s+only\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotAnAdvert();

    [GeneratedRegex(@"\b(?:advertiser|brand|sponsor|company|product|title|name)\s+(?:not\s+(?:shown|identified|visible|in\s+clip|on\s+screen)|never\s+shown|unknown|unclear)\b|\bno\s+(?:advertiser|brand|sponsor)(?:\s+(?:shown|card|visible|on\s+screen))?\b|\bnot\s+identified\b|\bunidentified\b|\bunknown\s+(?:advertiser|brand|sponsor|product)\b|\bunbranded\b|\bfilm\s+trailer\b|\btrailer\b|^unknown\b|\bunnamed\b", RegexOptions.IgnoreCase)]
    private static partial Regex Unbranded();

    [GeneratedRegex(@"\bPSA\b")]
    private static partial Regex Psa();

    [GeneratedRegex(@"\bpromo\b", RegexOptions.IgnoreCase)]
    private static partial Regex Promo();

    [GeneratedRegex(@"\s*[-–]\s*")]
    private static partial Regex DashSeparator();

    [GeneratedRegex(@"\s*[-–_]\s*|\s{2,}")]
    private static partial Regex Separators();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlug();

    public static ParsedName Parse(string path)
    {
        string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
        string cleaned = DuplicateSuffix().Replace(TechnicalParens().Replace(fileName, ""), "").Trim();
        IReadOnlyList<string> folders = Folders(path);
        var tags = new List<string>();

        DateOnly? airDate = null;
        Match dateMatch = IsoDate().Match(cleaned);
        if (dateMatch.Success && DateOnly.TryParseExact(dateMatch.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
            airDate = d;
        string withoutDate = dateMatch.Success ? cleaned.Remove(dateMatch.Index, dateMatch.Length) : cleaned;

        int? year = airDate?.Year ?? SingleYear(withoutDate);

        // A decade in the name or a folder ("90s") becomes a tag even when no exact year is known.
        foreach (string text in new[] { withoutDate }.Concat(folders))
        {
            Match dm = Decade().Match(text);
            if (dm.Success)
            {
                string century = dm.Groups["c"].Success ? dm.Groups["c"].Value : (int.Parse(dm.Groups["d"].Value) >= 3 ? "19" : "20");
                tags.Add($"{century}{dm.Groups["d"].Value}0s");
                break;
            }
        }

        bool looksLikeCommercial = CommercialWords().IsMatch(withoutDate) || folders.Any(f => CommercialWords().IsMatch(f));
        Match se = SeasonEpisode().Match(withoutDate);
        if (!se.Success) se = SeasonXEpisode().Match(withoutDate);

        // "90s Commercials - S02.E61" is a numbered commercial, not an episode of a show.
        if (se.Success && !looksLikeCommercial)
            return ParseEpisode(withoutDate, se, folders, airDate, year, tags);

        if (NotAnAdvert().IsMatch(withoutDate))
        {
            tags.Add("not-an-advert");
            return new ParsedName { Kind = MediaKind.Other, KindSource = FactSource.Filename, Title = TidyTitle(withoutDate), Year = year, Tags = Distinct(tags) };
        }

        (MediaKind kind, string kindSource) = ClassifyNonEpisode(withoutDate, folders);

        if (kind == MediaKind.Commercial)
        {
            if (Psa().IsMatch(withoutDate)) tags.Add("psa");
            if (Promo().IsMatch(withoutDate)) tags.Add("promo");
            if (Unbranded().IsMatch(withoutDate))
                tags.Add("unbranded");
            else if (Brand(withoutDate) is { } brand)
                tags.Add("brand:" + brand);
        }

        return new ParsedName
        {
            Kind = kind,
            KindSource = kindSource,
            Title = TidyTitle(withoutDate),
            Year = year,
            Tags = Distinct(tags),
        };
    }

    private static ParsedName ParseEpisode(string name, Match se, IReadOnlyList<string> folders, DateOnly? airDate, int? year, List<string> tags)
    {
        int season = int.Parse(se.Groups["s"].Value, CultureInfo.InvariantCulture);
        int episode = int.Parse(se.Groups["e"].Value, CultureInfo.InvariantCulture);

        string before = name[..se.Index];
        string after = name[(se.Index + se.Length)..];

        // Show is the last meaningful segment before SxxEyy: "257 - TNG" -> "TNG".
        string? show = Segments(before).LastOrDefault(s => !IsNumeric(s));
        string? showSource = show is null ? null : FactSource.Filename;
        if (show is null)
        {
            show = ShowFromFolders(folders);
            showSource = show is null ? null : FactSource.Folder;
        }

        string? episodeTitle = Segments(after).FirstOrDefault();

        return new ParsedName
        {
            Kind = MediaKind.Episode,
            KindSource = FactSource.Filename,
            Title = show is null ? TidyTitle(name) : $"{show} S{season:00}E{episode:00}" + (episodeTitle is null ? "" : $" {episodeTitle}"),
            Year = year,
            Show = show,
            ShowSource = showSource,
            Season = season,
            Episode = episode,
            AirDate = airDate,
            EpisodeTitle = episodeTitle,
            Tags = Distinct(tags),
        };
    }

    private static (MediaKind, string) ClassifyNonEpisode(string name, IReadOnlyList<string> folders)
    {
        if (BumperWords().IsMatch(name)) return (MediaKind.Bumper, FactSource.Filename);
        if (FillerWords().IsMatch(name)) return (MediaKind.Filler, FactSource.Filename);
        if (CommercialWords().IsMatch(name)) return (MediaKind.Commercial, FactSource.Filename);

        foreach (string folder in folders)
        {
            if (BumperWords().IsMatch(folder)) return (MediaKind.Bumper, FactSource.Folder);
            if (FillerWords().IsMatch(folder)) return (MediaKind.Filler, FactSource.Folder);
            if (CommercialWords().IsMatch(folder)) return (MediaKind.Commercial, FactSource.Folder);
        }
        return (MediaKind.Unknown, FactSource.Default);
    }

    /// <summary>"Ross Perot - United We Stand America, 1-800-..." gives brand "ross-perot". Only when a dash splits brand from description.</summary>
    private static string? Brand(string name)
    {
        string[] parts = DashSeparator().Split(name, 2);
        if (parts.Length < 2) return null;
        string head = parts[0].Trim();
        if (head.Length == 0 || head.Length > 60 || IsNumeric(head)) return null;
        // "Watch advert", "Night highway footage": generic descriptions are not brands.
        if (CommercialWords().IsMatch(head) || head.Contains("footage", StringComparison.OrdinalIgnoreCase)) return null;
        string slug = NonSlug().Replace(head.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? null : slug;
    }

    /// <summary>One year in the name is a fact; several (a retrospective listing 1985, 1987, 1990) is noise.</summary>
    private static int? SingleYear(string name)
    {
        var years = Year().Matches(name).Select(m => int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture)).Distinct().ToList();
        return years.Count == 1 ? years[0] : null;
    }

    private static string? ShowFromFolders(IReadOnlyList<string> folders) =>
        folders.FirstOrDefault(f => !SeasonFolder().IsMatch(f.Trim()));

    private static IReadOnlyList<string> Folders(string path)
    {
        var result = new List<string>();
        string? dir = System.IO.Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            string name = System.IO.Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name)) break;
            result.Add(name);
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        return result;
    }

    private static IEnumerable<string> Segments(string s) =>
        Separators().Split(s).Select(x => x.Trim()).Where(x => x.Length > 0);

    private static bool IsNumeric(string s) => s.Length > 0 && s.All(char.IsDigit);

    private static List<string> Distinct(List<string> tags) => tags.Distinct().ToList();

    private static string TidyTitle(string s)
    {
        string t = Separators().Replace(s, " ").Trim();
        string[] parts = t.Split(' ', 2);
        if (parts.Length == 2 && IsNumeric(parts[0])) t = parts[1];
        return t;
    }
}
