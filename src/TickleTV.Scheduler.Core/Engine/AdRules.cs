using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using YamlDotNet.Serialization;

namespace TickleTV.Scheduler.Core.Engine;

/// <summary>Which commercials a time slot may air, by category. Toys and cereal in the cartoons, nothing from the beer aisle before 9pm.</summary>
public sealed class SlotAdsConfig
{
    /// <summary>Category slugs. When set, only commercials in one of these categories air in the slot. The slug <c>none</c> means commercials with no category yet.</summary>
    public List<string> Only { get; set; } = [];
    /// <summary>Category slugs that never air in the slot.</summary>
    public List<string> Never { get; set; } = [];

    [YamlIgnore]
    public bool IsEmpty => Only.Count == 0 && Never.Count == 0;

    public void Normalize()
    {
        Only = AdRules.CleanSlugs(Only);
        Never = AdRules.CleanSlugs(Never);
    }
}

/// <summary>Applies the channel's and a slot's category rules to commercials.</summary>
public static class AdRules
{
    public const string CategoryPrefix = "category:";
    /// <summary>The pseudo-category for commercials that have no category tag.</summary>
    public const string NoCategory = "none";

    /// <summary>How few free commercials a slot can have before its breaks fall back to any commercial.</summary>
    public const int FallbackBelow = 4;
    /// <summary>A slot whose categories hold fewer commercials than this gets a warning at build time.</summary>
    public const int SmallPool = 10;

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

    /// <summary>
    /// May this clip air under the channel's never-list and the slot's rules? Bumpers, filler and episodes always
    /// pass; the rules are about commercials (and unlabelled files used as commercials). With <paramref name="strict"/>
    /// off, the slot's only-list is ignored but every never-list still holds: that is the fallback when a slot's
    /// categories run short.
    /// </summary>
    public static bool Allowed(CatalogEntry entry, AdsConfig channel, SlotAdsConfig? slot, bool strict = true)
    {
        if (entry.Item.Kind is MediaKind.Bumper or MediaKind.Filler or MediaKind.Episode) return true;
        IReadOnlyList<string> categories = CategoriesOf(entry);
        if (channel.Never.Count > 0 && InAny(categories, channel.Never)) return false;
        if (slot is null) return true;
        if (slot.Never.Count > 0 && InAny(categories, slot.Never)) return false;
        if (strict && slot.Only.Count > 0 && !InAny(categories, slot.Only)) return false;
        return true;
    }

    /// <summary>"only Toys, Food" or "never Alcohol", for warnings and notes. <paramref name="name"/> turns a slug into words.</summary>
    public static string Describe(SlotAdsConfig slot, Func<string, string>? name = null)
    {
        name ??= Words;
        var parts = new List<string>();
        if (slot.Only.Count > 0) parts.Add("only " + string.Join(", ", slot.Only.Select(name)));
        if (slot.Never.Count > 0) parts.Add("never " + string.Join(", ", slot.Never.Select(name)));
        return string.Join("; ", parts);
    }

    /// <summary>"breakfast-cereal" as "Breakfast Cereal"; the no-category slug as words too.</summary>
    public static string Words(string slug) => slug == NoCategory
        ? "No category yet"
        : string.Join(" ", slug.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
