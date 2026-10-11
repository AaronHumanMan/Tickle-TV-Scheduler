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
    /// <summary>Tags derived from the name and folders, such as brand:ross-perot, category:cars, psa, unbranded, 1990s.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>
/// Pulls show, season, episode, air date, year, kind, and tags out of a file name and its folders.
/// Heuristic by design: anything it gets wrong is fixed once with a manual tag, which wins thereafter.
/// </summary>
public static partial class FilenameParser
{
    // "(480p_30fps_H264-128kbit_AAC)" and similar technical suffixes in brackets.
    [GeneratedRegex(@"\s*[\(\[][^\)\]]*(?:\d{3,4}p|x26[45]|h\.?26[45]|hevc|aac|ac3|web-?dl|bluray|hdtv|kbit)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalParens();

    // Scene-release tokens: everything from the first one to the end of the name is technical.
    [GeneratedRegex(@"\b(?:480p|576p|720p|1080p|2160p|4k|hdtv|pdtv|web-?dl|web-?rip|webrip|web|bluray|blu-ray|bdrip|brrip|dvdrip|dvd|hdrip|x264|x265|h\.?264|h\.?265|hevc|avc|xvid|divx|aac(?:\d\.\d)?|ac3|dd5\.1|dts|flac(?:\s?\d\.\d)?|remux|proper|repack|internal|limited|uncut|multi|dubbed|subbed)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex SceneTail();

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

    [GeneratedRegex(@"^(?:season|series)\s*\d+$|^s\d{1,2}$|~\s*season\s*\d+$", RegexOptions.IgnoreCase)]
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

    [GeneratedRegex(@"\b(?:advertiser|brand|sponsor|company|product|title|name)\s+(?:not\s+(?:shown|identified|visible|in\s+clip|on\s+screen)|never\s+shown|unknown|unclear)\b|\bno\s+(?:advertiser|brand|sponsor)(?:\s+(?:shown|card|visible|on\s+screen))?\b|\bnot\s+identified\b|\bunidentified\b|\bunknown\s+(?:advertiser|brand|sponsor|product)\b|\bunbranded\b|^unknown\b|\bunnamed\b", RegexOptions.IgnoreCase)]
    private static partial Regex Unbranded();

    [GeneratedRegex(@"\bPSA\b")]
    private static partial Regex Psa();

    [GeneratedRegex(@"\bpromos?\b|\bpremieres?\b|\ball[\s-]new\b|\b(?:tonight|tomorrow|weekdays|weeknights|mornings?|nights?)\b|\b(?:mon|tues|wednes|thurs|fri|satur|sun)days?\b|\b\d{1,2}(?::\d{2})?\s?(?:am|pm)\b|\b\d{1,2}(?:-\d{2})?[/-]\d{1,2}c\b|\bnext\s+(?:week|episode)\b|\bcoming\b|\bmarathon\b", RegexOptions.IgnoreCase)]
    private static partial Regex PromoWords();

    [GeneratedRegex(@"\bstation\s*id\b|\bidents?\b|\bID\b", RegexOptions.None)]
    private static partial Regex StationIdWords();

    [GeneratedRegex(@"\bbumpers?\b|\bwe.?ll\s+be\s+right\s+back\b|\bright\s+back\b|\bstay\s+tuned\b|\bnow\s+back\s+to\b|\bcoming\s+up\b|\bup\s+next\b", RegexOptions.IgnoreCase)]
    private static partial Regex BreakBumperWords();

    [GeneratedRegex(@"\s*[-–]\s*")]
    private static partial Regex DashSeparator();

    [GeneratedRegex(@"\s*[-–_]\s*|\s{2,}")]
    private static partial Regex Separators();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlug();

    [GeneratedRegex(@"^(?<y>(?:19|20)\d{2})\b")]
    private static partial Regex LeadingYear();

    public static ParsedName Parse(string path)
    {
        string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
        bool scene = IsSceneName(fileName);
        string cleaned = scene ? fileName.Replace('.', ' ') : fileName;
        cleaned = DuplicateSuffix().Replace(TechnicalParens().Replace(cleaned, ""), "").Trim();
        if (scene) cleaned = SceneTail().Replace(cleaned, "").Trim();

        IReadOnlyList<string> folders = Folders(path);
        var tags = new List<string>();

        DateOnly? airDate = null;
        Match dateMatch = IsoDate().Match(cleaned);
        if (dateMatch.Success && DateOnly.TryParseExact(dateMatch.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
            airDate = d;
        string withoutDate = dateMatch.Success ? cleaned.Remove(dateMatch.Index, dateMatch.Length) : cleaned;

        // Year: the air date, else one year in the name, else a folder that starts with a year ("2003 Commercials").
        int? year = airDate?.Year ?? SingleYear(withoutDate) ?? YearFromFolders(folders);

        // Decade tag: from the year when known, else the nearest decade folder, else the name.
        string? decade = year is { } yr ? $"{yr / 10 * 10}s" : DecadeFrom(folders) ?? DecadeFrom([withoutDate]);
        if (decade is not null) tags.Add(decade);

        // The folder directly under a decade folder is a category: "90s/Cars" gives category:cars.
        if (CategoryFolder(folders) is { } category) tags.Add("category:" + category);

        bool inBumperFolder = folders.Any(f => BumperWords().IsMatch(f));
        bool looksLikeCommercial = CommercialWords().IsMatch(withoutDate) || folders.Any(f => CommercialWords().IsMatch(f));
        Match se = SeasonEpisode().Match(withoutDate);
        if (!se.Success) se = SeasonXEpisode().Match(withoutDate);

        // "90s Commercials - S02.E61" is a numbered commercial, not an episode of a show.
        if (se.Success && !looksLikeCommercial && !inBumperFolder)
            return ParseEpisode(withoutDate, se, folders, scene, airDate, year, tags);

        if (NotAnAdvert().IsMatch(withoutDate))
        {
            tags.Add("not-an-advert");
            return new ParsedName { Kind = MediaKind.Other, KindSource = FactSource.Filename, Title = TidyTitle(withoutDate), Year = year, Tags = Distinct(tags) };
        }

        (MediaKind kind, string kindSource) = ClassifyNonEpisode(withoutDate, folders);

        if (kind == MediaKind.Commercial)
        {
            if (Psa().IsMatch(withoutDate)) tags.Add("psa");
            if (PromoWords().IsMatch(withoutDate) && Regex.IsMatch(withoutDate, @"\bpromos?\b", RegexOptions.IgnoreCase)) tags.Add("promo");
            if (Unbranded().IsMatch(withoutDate))
                tags.Add("unbranded");
            else if (Brand(withoutDate) is { } brand)
                tags.Add("brand:" + brand);
            if (Regex.IsMatch(withoutDate, @"\btrailer\b", RegexOptions.IgnoreCase) || folders.Any(f => f.Contains("trailer", StringComparison.OrdinalIgnoreCase)))
                tags.Add("trailer");
        }
        else if (kind == MediaKind.Bumper)
        {
            // Network promos ("NYPD Blue all new Tuesday 10-9c") are not break bumpers; station IDs and
            // "we'll be right back" cards are. Pools tell them apart by tag.
            if (PromoWords().IsMatch(withoutDate)) tags.Add("promo");
            if (StationIdWords().IsMatch(withoutDate)) tags.Add("station-id");
            if (BreakBumperWords().IsMatch(withoutDate)) tags.Add("break-card");
            if (Network(withoutDate) is { } network) tags.Add("network:" + network);
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

    private static ParsedName ParseEpisode(string name, Match se, IReadOnlyList<string> folders, bool scene, DateOnly? airDate, int? year, List<string> tags)
    {
        int season = int.Parse(se.Groups["s"].Value, CultureInfo.InvariantCulture);
        int episode = int.Parse(se.Groups["e"].Value, CultureInfo.InvariantCulture);

        string before = name[..se.Index];
        string after = name[(se.Index + se.Length)..];

        // Show: for scene names the folder is the clean source ("30.For.30" vs "30 for 30"); otherwise the last
        // meaningful segment before SxxEyy ("257 - TNG" gives "TNG"), falling back to the folder.
        string? show = null;
        string? showSource = null;
        if (scene && ShowFromFolders(folders) is { } folderShow)
        {
            show = folderShow;
            showSource = FactSource.Folder;
        }
        if (show is null)
        {
            show = Segments(before).LastOrDefault(s => !IsNumeric(s))?.Trim('.', ' ');
            showSource = show is null ? null : FactSource.Filename;
        }
        if (string.IsNullOrEmpty(show))
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

        // Nearest folder first: "90s/Bumpers and Promos" beats the "Commercials" root.
        foreach (string folder in folders)
        {
            if (BumperWords().IsMatch(folder)) return (MediaKind.Bumper, FactSource.Folder);
            if (FillerWords().IsMatch(folder)) return (MediaKind.Filler, FactSource.Folder);
            if (CommercialWords().IsMatch(folder)) return (MediaKind.Commercial, FactSource.Folder);
        }

        if (CommercialWords().IsMatch(name)) return (MediaKind.Commercial, FactSource.Filename);
        return (MediaKind.Unknown, FactSource.Default);
    }

    /// <summary>"Ross Perot - United We Stand America, 1-800-..." gives brand "ross-perot". Only when a dash splits brand from description.</summary>
    private static string? Brand(string name)
    {
        string[] parts = DashSeparator().Split(name, 2);
        if (parts.Length < 2)
        {
            // "Aetna insurance" or "Hyundai Cat Commercial 1992": a short dash-less title is the brand itself,
            // once filler words and the year are dropped. Long descriptions are not.
            string bare = Year().Replace(CommercialWords().Replace(name, ""), "").Trim();
            bare = Regex.Replace(bare, @"\s{2,}", " ");
            int words = bare.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (words is >= 1 and <= 5 && !Unbranded().IsMatch(bare) && !IsNumeric(bare)) return Slug(bare);
            return null;
        }
        string head = parts[0].Trim();
        if (head.Length == 0 || head.Length > 60 || IsNumeric(head)) return null;
        if (CommercialWords().IsMatch(head) || head.Contains("footage", StringComparison.OrdinalIgnoreCase)) return null;
        string slug = Slug(head);
        return slug.Length == 0 ? null : slug;
    }

    /// <summary>"ABC - Street Hawk premieres Friday" gives network "abc"; "KMPH 26 - ..." gives "kmph-26".</summary>
    private static string? Network(string name)
    {
        string[] parts = DashSeparator().Split(name, 2);
        if (parts.Length < 2) return null;
        string head = parts[0].Trim();
        if (head.Length == 0 || head.Length > 40) return null;
        string slug = Slug(head);
        return slug.Length == 0 ? null : slug;
    }

    private static string Slug(string s) => NonSlug().Replace(s.ToLowerInvariant(), "-").Trim('-');

    /// <summary>One year in the name is a fact; several (a retrospective listing 1985, 1987, 1990) is noise.</summary>
    private static int? SingleYear(string name)
    {
        var years = Year().Matches(name).Select(m => int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture)).Distinct().ToList();
        return years.Count == 1 ? years[0] : null;
    }

    private static int? YearFromFolders(IReadOnlyList<string> folders)
    {
        foreach (string f in folders)
        {
            Match m = LeadingYear().Match(f.Trim());
            if (m.Success) return int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
        }
        return null;
    }

    private static string? DecadeFrom(IEnumerable<string> texts)
    {
        foreach (string text in texts)
        {
            Match dm = Decade().Match(text);
            if (!dm.Success) continue;
            string century = dm.Groups["c"].Success ? dm.Groups["c"].Value : (int.Parse(dm.Groups["d"].Value) >= 3 ? "19" : "20");
            return $"{century}{dm.Groups["d"].Value}0s";
        }
        return null;
    }

    /// <summary>The folder just below a decade folder, slugged: "Health Beauty Personal Care" gives "health-beauty-personal-care".</summary>
    private static string? CategoryFolder(IReadOnlyList<string> folders)
    {
        for (int i = 0; i + 1 < folders.Count; i++)
        {
            if (Decade().IsMatch(folders[i + 1]) && !Decade().IsMatch(folders[i]))
            {
                string slug = Slug(folders[i]);
                return slug.Length == 0 ? null : slug;
            }
        }
        return null;
    }

    /// <summary>Scene releases have no spaces and dot separators: "Good.Eats.S10E03.1080p.WEB-DL".</summary>
    private static bool IsSceneName(string fileName) =>
        !fileName.Contains(' ') && fileName.Count(c => c == '.') >= 2;

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
