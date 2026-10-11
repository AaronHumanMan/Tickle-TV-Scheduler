using System.Globalization;
using System.Text.RegularExpressions;

namespace TickleTV.Scheduler.Core.Catalog;

/// <summary>
/// A small filter language for pools, evaluated against catalog entries. Terms are joined with AND;
/// a term may be negated with NOT. Supported terms:
/// <list type="bullet">
/// <item><c>kind = commercial</c></item>
/// <item><c>tag:kids</c> or <c>tag = kids</c></item>
/// <item><c>show = TNG</c></item>
/// <item><c>year = 1991</c>, <c>year &gt;= 1988</c>, <c>year &lt;= 1994</c>, <c>year between 1988 and 1994</c></item>
/// <item><c>era within 3</c>: year within N years of the airing episode's year (needs context)</item>
/// <item><c>duration &lt;= 60s</c>, <c>duration &gt;= 10s</c></item>
/// </list>
/// </summary>
public sealed partial class PoolQuery
{
    private readonly List<(bool negate, Func<CatalogEntry, QueryContext, bool> test)> _terms = [];
    public string Source { get; }

    private PoolQuery(string source) => Source = source;

    public static PoolQuery Parse(string text)
    {
        var q = new PoolQuery(text);
        string protectedText = BetweenAnd().Replace(text, "between ${a}..${b}");
        foreach (string raw in SplitAnd().Split(protectedText))
        {
            string term = raw.Trim();
            if (term.Length == 0) continue;
            bool negate = false;
            if (term.StartsWith("NOT ", StringComparison.OrdinalIgnoreCase))
            {
                negate = true;
                term = term[4..].Trim();
            }
            q._terms.Add((negate, ParseTerm(term)));
        }
        return q;
    }

    public bool Matches(CatalogEntry entry, QueryContext? context = null)
    {
        QueryContext ctx = context ?? QueryContext.None;
        foreach ((bool negate, var test) in _terms)
        {
            bool r = test(entry, ctx);
            if (negate ? r : !r) return false;
        }
        return true;
    }

    public IEnumerable<CatalogEntry> Filter(IEnumerable<CatalogEntry> entries, QueryContext? context = null) =>
        entries.Where(e => Matches(e, context));

    private static Func<CatalogEntry, QueryContext, bool> ParseTerm(string term)
    {
        Match m;
        if ((m = TagTerm().Match(term)).Success)
        {
            string tag = CatalogDb.NormalizeTag(m.Groups["v"].Value);
            return (e, _) => e.Tags.Any(t => t.Name == tag);
        }
        if ((m = KindTerm().Match(term)).Success)
        {
            if (!Enum.TryParse(m.Groups["v"].Value, ignoreCase: true, out MediaKind kind))
                throw new FormatException($"Unknown kind '{m.Groups["v"].Value}' in pool query.");
            return (e, _) => e.Item.Kind == kind;
        }
        if ((m = ShowTerm().Match(term)).Success)
        {
            string show = m.Groups["v"].Value.Trim().Trim('"', '\'');
            return (e, _) => string.Equals(e.Episode?.Show, show, StringComparison.OrdinalIgnoreCase);
        }
        if ((m = YearBetween().Match(term)).Success)
        {
            int lo = Int(m.Groups["a"].Value), hi = Int(m.Groups["b"].Value);
            return (e, _) => e.Item.Year is { } y && y >= lo && y <= hi;
        }
        if ((m = YearCompare().Match(term)).Success)
        {
            int n = Int(m.Groups["v"].Value);
            string op = m.Groups["op"].Value;
            return (e, _) => e.Item.Year is { } y && Compare(y, op, n);
        }
        if ((m = EraWithin().Match(term)).Success)
        {
            int span = Int(m.Groups["v"].Value);
            return (e, ctx) => ctx.ReferenceYear is { } r && e.Item.Year is { } y && Math.Abs(y - r) <= span;
        }
        if ((m = DurationCompare().Match(term)).Success)
        {
            long ms = (long)(double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture) * 1000);
            string op = m.Groups["op"].Value;
            return (e, _) => Compare(e.Item.DurationMs, op, ms);
        }
        throw new FormatException($"Cannot parse pool query term '{term}'.");
    }

    private static bool Compare(long value, string op, long n) => op switch
    {
        "=" or "==" => value == n,
        ">=" => value >= n,
        "<=" => value <= n,
        ">" => value > n,
        "<" => value < n,
        "!=" or "<>" => value != n,
        _ => throw new FormatException($"Unknown operator '{op}'."),
    };

    private static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\s+AND\s+", RegexOptions.IgnoreCase)]
    private static partial Regex SplitAnd();

    [GeneratedRegex(@"^tag\s*[:=]\s*(?<v>[^\s]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TagTerm();

    [GeneratedRegex(@"^kind\s*=\s*(?<v>\w+)$", RegexOptions.IgnoreCase)]
    private static partial Regex KindTerm();

    [GeneratedRegex(@"^show\s*=\s*(?<v>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShowTerm();

    [GeneratedRegex(@"^year\s+between\s+(?<a>\d{4})\.\.(?<b>\d{4})$", RegexOptions.IgnoreCase)]
    private static partial Regex YearBetween();

    [GeneratedRegex(@"between\s+(?<a>\d{4})\s+and\s+(?<b>\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex BetweenAnd();

    [GeneratedRegex(@"^year\s*(?<op>>=|<=|=|==|!=|<>|>|<)\s*(?<v>\d{4})$", RegexOptions.IgnoreCase)]
    private static partial Regex YearCompare();

    [GeneratedRegex(@"^era\s+within\s+(?<v>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex EraWithin();

    [GeneratedRegex(@"^duration\s*(?<op>>=|<=|=|==|>|<)\s*(?<v>\d+(?:\.\d+)?)\s*s$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationCompare();
}

/// <summary>Facts about the airing context a relative term can use, such as the episode's year for era matching.</summary>
public sealed record QueryContext(int? ReferenceYear)
{
    public static readonly QueryContext None = new((int?)null);
}
