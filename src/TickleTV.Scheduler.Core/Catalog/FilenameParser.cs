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
}

/// <summary>
/// Pulls show, season, episode, air date, year, and kind out of a file name and its folders.
/// Heuristic by design: anything it gets wrong is fixed once with a manual tag, which wins thereafter.
/// </summary>
public static partial class FilenameParser
{
    // "(480p_30fps_H264-128kbit_AAC)" and similar technical suffixes.
    [GeneratedRegex(@"\s*[\(\[][^\)\]]*(?:\d{3,4}p|x26[45]|h\.?26[45]|hevc|aac|ac3|web-?dl|bluray|hdtv|kbit)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalParens();

    [GeneratedRegex(@"\bS(?<s>\d{1,2})\s?E(?<e>\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisode();

    [GeneratedRegex(@"\b(?<s>\d{1,2})x(?<e>\d{2,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonXEpisode();

    [GeneratedRegex(@"\b(?<y>(?:19|20)\d{2})-(?<m>\d{2})-(?<d>\d{2})\b")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})(?!\d)")]
    private static partial Regex Year();

    [GeneratedRegex(@"^(?:season|series)\s*\d+$|^s\d{1,2}$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"\b(commercials?|advert(?:isement)?s?|ads?|psa|promos?|spots?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CommercialWords();

    [GeneratedRegex(@"\b(bumpers?|idents?|station\s*id|interstitials?|stingers?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BumperWords();

    [GeneratedRegex(@"\b(fillers?|fallback|slate)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FillerWords();

    [GeneratedRegex(@"\s*[-–_]\s*|\s{2,}")]
    private static partial Regex Separators();

    public static ParsedName Parse(string path)
    {
        string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
        string cleaned = TechnicalParens().Replace(fileName, "").Trim();
        IReadOnlyList<string> folders = Folders(path);

        DateOnly? airDate = null;
        Match dateMatch = IsoDate().Match(cleaned);
        if (dateMatch.Success && DateOnly.TryParseExact(dateMatch.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
            airDate = d;

        string withoutDate = dateMatch.Success ? cleaned.Remove(dateMatch.Index, dateMatch.Length) : cleaned;

        int? year = airDate?.Year;
        if (year is null)
        {
            Match y = Year().Match(withoutDate);
            if (y.Success) year = int.Parse(y.Groups["y"].Value, CultureInfo.InvariantCulture);
        }

        Match se = SeasonEpisode().Match(withoutDate);
        if (!se.Success) se = SeasonXEpisode().Match(withoutDate);

        if (se.Success)
            return ParseEpisode(withoutDate, se, folders, airDate, year);

        (MediaKind kind, string kindSource) = ClassifyNonEpisode(withoutDate, folders);
        return new ParsedName
        {
            Kind = kind,
            KindSource = kindSource,
            Title = TidyTitle(withoutDate),
            Year = year,
        };
    }

    private static ParsedName ParseEpisode(string name, Match se, IReadOnlyList<string> folders, DateOnly? airDate, int? year)
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
        };
    }

    private static (MediaKind, string) ClassifyNonEpisode(string name, IReadOnlyList<string> folders)
    {
        if (CommercialWords().IsMatch(name)) return (MediaKind.Commercial, FactSource.Filename);
        if (BumperWords().IsMatch(name)) return (MediaKind.Bumper, FactSource.Filename);
        if (FillerWords().IsMatch(name)) return (MediaKind.Filler, FactSource.Filename);

        foreach (string folder in folders)
        {
            if (CommercialWords().IsMatch(folder)) return (MediaKind.Commercial, FactSource.Folder);
            if (BumperWords().IsMatch(folder)) return (MediaKind.Bumper, FactSource.Folder);
            if (FillerWords().IsMatch(folder)) return (MediaKind.Filler, FactSource.Folder);
        }
        return (MediaKind.Unknown, FactSource.Default);
    }

    /// <summary>Nearest ancestor folder that is not a season folder: "The Simpsons/Season 1/x.mkv" gives "The Simpsons".</summary>
    private static string? ShowFromFolders(IReadOnlyList<string> folders) =>
        folders.FirstOrDefault(f => !SeasonFolder().IsMatch(f.Trim()));

    /// <summary>Folders nearest-first, excluding drive roots.</summary>
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

    private static bool IsNumeric(string s) => s.All(char.IsDigit);

    private static string TidyTitle(string s)
    {
        string t = Separators().Replace(s, " ").Trim();
        // Drop a leading catalog index such as "257".
        string[] parts = t.Split(' ', 2);
        if (parts.Length == 2 && IsNumeric(parts[0])) t = parts[1];
        return t;
    }
}
