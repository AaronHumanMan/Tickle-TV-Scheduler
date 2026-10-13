using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using YamlDotNet.Serialization;

namespace TickleTV.Scheduler.Core.Engine;

/// <summary>Which commercials a time slot may air, by category and by year. Toys and cereal in the cartoons, nothing from the beer aisle before 9pm, and 1960s spots around a 1960s show.</summary>
public sealed class SlotAdsConfig
{
    /// <summary>Category slugs. When set, only commercials in one of these categories air in the slot. The slug <c>none</c> means commercials with no category yet.</summary>
    public List<string> Only { get; set; } = [];
    /// <summary>Category slugs that never air in the slot.</summary>
    public List<string> Never { get; set; } = [];
    /// <summary>
    /// Which years the slot's commercials may come from. <c>show</c>: from around the year the airing episode was made
    /// (see <see cref="EraWithin"/>). <c>decades</c>: only the <see cref="Decades"/> listed. Null means any year.
    /// </summary>
    public string? Era { get; set; }
    /// <summary>For <c>era: show</c>, how many years either side of the episode's year still count as its era. Default 3.</summary>
    public int? EraWithin { get; set; }
    /// <summary>For <c>era: decades</c>, the decades allowed: "1980s", "1990s".</summary>
    public List<string> Decades { get; set; } = [];

    [YamlIgnore]
    public bool IsEmpty => Only.Count == 0 && Never.Count == 0 && EraMode == EraMode.Any;

    [YamlIgnore]
    public EraMode EraMode => (Era ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "any" => EraMode.Any,
        "show" or "episode" or "match" => EraMode.Show,
        "decades" or "decade" => EraMode.Decades,
        _ => throw new InvalidDataException($"Unknown era '{Era}'. Use show, decades, or leave it out."),
    };

    /// <summary>The years either side of the episode's year that count as its era (<c>era: show</c>).</summary>
    [YamlIgnore]
    public int EraSpan => Math.Clamp(EraWithin ?? AdRules.DefaultEraWithin, 0, 50);

    public void Normalize()
    {
        Only = AdRules.CleanSlugs(Only);
        Never = AdRules.CleanSlugs(Never);
        Decades = Decades.Select(AdRules.DecadeTag).OfType<string>().Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal).ToList();
        switch (EraMode)
        {
            case EraMode.Show:
                Era = "show";
                EraWithin = EraSpan == AdRules.DefaultEraWithin ? null : EraSpan;
                Decades = [];
                break;
            case EraMode.Decades when Decades.Count > 0:
                Era = "decades";
                EraWithin = null;
                break;
            default:
                // "any", or decades with nothing ticked: no year rule, and nothing about it in the file.
                Era = null;
                EraWithin = null;
                Decades = [];
                break;
        }
    }
}

/// <summary>How a slot limits its commercials by year.</summary>
public enum EraMode { Any, Show, Decades }

/// <summary>Applies the channel's and a slot's category and year rules to commercials.</summary>
public static class AdRules
{
    public const string CategoryPrefix = "category:";
    /// <summary>The pseudo-category for commercials that have no category tag.</summary>
    public const string NoCategory = "none";

    /// <summary>How few free commercials a slot can have before its breaks fall back to any commercial.</summary>
    public const int FallbackBelow = 4;
    /// <summary>A slot whose categories hold fewer commercials than this gets a warning at build time.</summary>
    public const int SmallPool = 10;
    /// <summary>How many years either side of an episode's year count as its era unless the slot says otherwise.</summary>
    public const int DefaultEraWithin = 3;

    /// <summary>"Breakfast cereal" or "category:breakfast-cereal" to the slug "breakfast-cereal".</summary>
    public static string Slug(string category)
    {
        string s = CatalogDb.NormalizeTag(category);
        return s.StartsWith(CategoryPrefix, StringComparison.Ordinal) ? s[CategoryPrefix.Length..] : s;
    }

    public static List<string> CleanSlugs(IEnumerable<string> slugs) =>
        slugs.Where(s => !string.IsNullOrWhiteSpace(s)).Select(Slug).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The category slugs on a clip.</summary>
    public static IReadOnlyList<string> CategoriesOf(CatalogEntry entry) =>
        entry.Tags.Where(t => t.Name.StartsWith(CategoryPrefix, StringComparison.Ordinal)).Select(t => t.Name[CategoryPrefix.Length..]).ToList();

    private static bool InAny(IReadOnlyList<string> categories, IReadOnlyList<string> slugs) =>
        (categories.Count == 0 && slugs.Contains(NoCategory, StringComparer.Ordinal))
        || categories.Any(c => slugs.Contains(c, StringComparer.Ordinal));

    private static bool AlwaysPasses(CatalogEntry entry) => entry.Item.Kind is MediaKind.Bumper or MediaKind.Filler or MediaKind.Episode;

    /// <summary>
    /// May this clip air under the channel's never-list and the slot's rules? Bumpers, filler and episodes always
    /// pass; the rules are about commercials (and unlabelled files used as commercials). With <paramref name="strict"/>
    /// off, the slot's only-list and decades are ignored but every never-list still holds: that is the fallback when
    /// a slot's rules run short. The show-era rule needs the airing episode, see <see cref="InShowEra"/>.
    /// </summary>
    public static bool Allowed(CatalogEntry entry, AdsConfig channel, SlotAdsConfig? slot, bool strict = true)
    {
        if (AlwaysPasses(entry)) return true;
        IReadOnlyList<string> categories = CategoriesOf(entry);
        if (channel.Never.Count > 0 && InAny(categories, channel.Never)) return false;
        if (slot is null) return true;
        if (slot.Never.Count > 0 && InAny(categories, slot.Never)) return false;
        if (strict && slot.Only.Count > 0 && !InAny(categories, slot.Only)) return false;
        if (strict && slot.EraMode == EraMode.Decades && !InDecades(entry, slot.Decades)) return false;
        return true;
    }

    /// <summary>
    /// The years a clip could be from: its year when known, else the span of its decade tag ("1990s" is 1990 to 1999),
    /// else null when nothing in the catalog says when it was made.
    /// </summary>
    public static (int Lo, int Hi)? YearsOf(CatalogEntry entry)
    {
        if (entry.Item.Year is { } y) return (y, y);
        foreach (Tag t in entry.Tags)
            if (DecadeStart(t.Name) is { } d) return (d, d + 9);
        return null;
    }

    /// <summary>Is the clip from one of these decades? Clips with no year and no decade tag are not.</summary>
    public static bool InDecades(CatalogEntry entry, IReadOnlyList<string> decades)
    {
        if (AlwaysPasses(entry)) return true;
        if (YearsOf(entry) is not { } span) return false;
        foreach (string decade in decades)
            if (DecadeStart(decade) is { } d && span.Lo <= d + 9 && span.Hi >= d) return true;
        return false;
    }

    /// <summary>
    /// Is the clip from around <paramref name="referenceYear"/> (the airing episode's year), within the slot's span?
    /// A clip with no year and no decade tag is not. With no reference year there is nothing to match, so everything passes.
    /// </summary>
    public static bool InShowEra(CatalogEntry entry, SlotAdsConfig slot, int? referenceYear)
    {
        if (AlwaysPasses(entry)) return true;
        if (referenceYear is not { } r) return true;
        if (YearsOf(entry) is not { } span) return false;
        return span.Lo <= r + slot.EraSpan && span.Hi >= r - slot.EraSpan;
    }

    /// <summary>"80s", "1980s" or "1980" to the tag "1980s"; null when the text isn't a decade.</summary>
    public static string? DecadeTag(string text)
    {
        string s = text.Trim().TrimEnd('s', 'S').TrimEnd('\'', '’');
        if (!int.TryParse(s, out int n) || n < 0) return null;
        if (s.Length == 2) n += n >= 30 ? 1900 : 2000;
        else if (s.Length != 4) return null;
        return $"{n / 10 * 10}s";
    }

    /// <summary>The first year of a decade tag: "1990s" gives 1990. Null for any other tag.</summary>
    public static int? DecadeStart(string tag) =>
        tag.Length == 5 && tag[4] == 's' && int.TryParse(tag.AsSpan(0, 4), out int y) && y % 10 == 0 ? y : null;

    /// <summary>"only Toys, Food", "never Alcohol", "from around the show's year (within 3 years)" or "from the 1980s, 1990s", for warnings and notes. <paramref name="name"/> turns a slug into words.</summary>
    public static string Describe(SlotAdsConfig slot, Func<string, string>? name = null)
    {
        name ??= Words;
        var parts = new List<string>();
        if (slot.Only.Count > 0) parts.Add("only " + string.Join(", ", slot.Only.Select(name)));
        if (slot.Never.Count > 0) parts.Add("never " + string.Join(", ", slot.Never.Select(name)));
        if (EraWords(slot) is { } era) parts.Add(era);
        return string.Join("; ", parts);
    }

    /// <summary>The slot's year rule in words, or null when it takes any year.</summary>
    public static string? EraWords(SlotAdsConfig slot) => slot.EraMode switch
    {
        EraMode.Show => $"from around the show's year (within {slot.EraSpan} year{(slot.EraSpan == 1 ? "" : "s")})",
        EraMode.Decades => "from the " + string.Join(", ", slot.Decades),
        _ => null,
    };

    /// <summary>"breakfast-cereal" as "Breakfast Cereal"; the no-category slug as words too.</summary>
    public static string Words(string slug) => slug == NoCategory
        ? "No category yet"
        : string.Join(" ", slug.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
