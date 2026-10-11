using System.Text.Json;
using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;

namespace TickleTV.Scheduler.Core.Engine;

/// <summary>
/// Picks the next episode of a show for a channel and remembers where it got to.
/// Chronological walks broadcast order and wraps. Shuffle draws without replacement: every episode
/// airs once before a new cycle is shuffled. State lives in the catalog's rotation table.
/// </summary>
public sealed class EpisodePicker
{
    private readonly CatalogDb _db;
    private readonly Random _random;

    public EpisodePicker(CatalogDb db, int? seed = null)
    {
        _db = db;
        _random = seed is null ? Random.Shared : new Random(seed.Value);
    }

    public CatalogEntry? Next(string show, string channel, OrderMode mode)
    {
        IReadOnlyList<CatalogEntry> episodes = _db.EpisodesOf(show);
        if (episodes.Count == 0) return null;

        var state = _db.GetRotation(show, channel);
        return mode == OrderMode.Chronological
            ? NextChronological(show, channel, episodes, state)
            : NextShuffle(show, channel, episodes, state);
    }

    private CatalogEntry NextChronological(string show, string channel, IReadOnlyList<CatalogEntry> episodes, (string Mode, int? NextPosition, string? CycleJson)? state)
    {
        int position = 0;
        if (state is { } s && s.Mode == nameof(OrderMode.Chronological) && s.NextPosition is { } p)
            position = p;
        else if (state is { } sh && sh.Mode == nameof(OrderMode.Shuffle))
            position = ResumeFromShuffle(episodes, sh.CycleJson);

        if (position >= episodes.Count) position = 0;
        CatalogEntry pick = episodes[position];
        _db.SetRotation(show, channel, nameof(OrderMode.Chronological), (position + 1) % episodes.Count, null);
        return pick;
    }

    private sealed record Cycle(List<long> Remaining, List<long> Members, long? Last = null);

    private static Cycle? ReadCycle(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<Cycle>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Switching from shuffle resumes at the first episode, in broadcast order, not yet aired in the current cycle.</summary>
    private static int ResumeFromShuffle(IReadOnlyList<CatalogEntry> episodes, string? cycleJson)
    {
        Cycle? cycle = ReadCycle(cycleJson);
        if (cycle is null) return 0;
        var remainingSet = cycle.Remaining.ToHashSet();
        for (int i = 0; i < episodes.Count; i++)
            if (remainingSet.Contains(episodes[i].Item.Id)) return i;
        return 0;
    }

    private CatalogEntry NextShuffle(string show, string channel, IReadOnlyList<CatalogEntry> episodes, (string Mode, int? NextPosition, string? CycleJson)? state)
    {
        var byId = episodes.ToDictionary(e => e.Item.Id);
        List<long> remaining = [];
        List<long> members = [];
        if (state is { } s && s.Mode == nameof(OrderMode.Shuffle) && ReadCycle(s.CycleJson) is { } cycle)
        {
            // Drop episodes that no longer exist; keep the rest of the cycle intact.
            remaining = cycle.Remaining.Where(byId.ContainsKey).ToList();
            members = cycle.Members.Where(byId.ContainsKey).ToList();
            // Episodes added to the catalog since the cycle started join it at random positions.
            var known = members.ToHashSet();
            foreach (CatalogEntry e in episodes.Where(e => !known.Contains(e.Item.Id)))
            {
                remaining.Insert(_random.Next(remaining.Count + 1), e.Item.Id);
                members.Add(e.Item.Id);
            }
        }

        long? last = state is { } st && st.Mode == nameof(OrderMode.Shuffle) ? ReadCycle(st.CycleJson)?.Last : null;
        if (remaining.Count == 0)
        {
            remaining = Shuffle(episodes.Select(e => e.Item.Id).ToList());
            members = remaining.ToList();
            // A new cycle must not open with the episode that just closed the old one.
            if (remaining.Count > 1 && remaining[0] == last)
            {
                int j = 1 + _random.Next(remaining.Count - 1);
                (remaining[0], remaining[j]) = (remaining[j], remaining[0]);
            }
        }

        long pickId = remaining[0];
        remaining.RemoveAt(0);
        _db.SetRotation(show, channel, nameof(OrderMode.Shuffle), null, JsonSerializer.Serialize(new Cycle(remaining, members, pickId)));
        return byId[pickId];
    }

    private List<long> Shuffle(List<long> ids)
    {
        for (int i = ids.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (ids[i], ids[j]) = (ids[j], ids[i]);
        }
        return ids;
    }
}
