using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Guide;
using TickleTV.Scheduler.Core.Media;
using TickleTV.Scheduler.Core.Playout;

namespace TickleTV.Scheduler.Core.Engine;

public sealed record PlayRecord(long ItemId, DateTimeOffset AiredAt, string Context);

public sealed record BuildResult
{
    public required string Channel { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public required Playout.Playout Playout { get; init; }
    public required IReadOnlyList<GuideProgramme> Programmes { get; init; }
    public required IReadOnlyList<PlayRecord> Plays { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Turns a channel's daily grid into a playout window. Each slot airs episodes of its show back to back,
/// each padded to its boundary, until the next slot's start time. Ads come from the channel's pool with a
/// cooldown so the same clip does not return too soon.
/// </summary>
public sealed class ChannelBuilder
{
    private readonly CatalogDb _db;
    private readonly EpisodePicker _picker;
    private readonly ChannelConfig _channel;
    private readonly string _schemaVersion;
    private readonly Action<string>? _log;

    public ChannelBuilder(CatalogDb db, EpisodePicker picker, ChannelConfig channel, string schemaVersion, Action<string>? log = null)
    {
        _db = db;
        _picker = picker;
        _channel = channel;
        _schemaVersion = schemaVersion;
        _log = log;
    }

    public BuildResult Build(DateTimeOffset from, DateTimeOffset until)
    {
        var items = new List<PlayoutItem>();
        var programmes = new List<GuideProgramme>();
        var plays = new List<PlayRecord>();
        var warnings = new List<string>();

        PoolQuery pool = PoolQuery.Parse(_channel.Ads.Pool);
        List<CatalogEntry> adLibrary = _db.ListItems(MediaKind.Commercial)
            .Concat(_db.ListItems(MediaKind.Unknown))
            .Select(i => _db.GetEntry(i.Id)!)
            .ToList();
        BumperSet bumperSet = LoadBumpers();
        List<CatalogEntry> bumperLibrary = _db.ListItems(MediaKind.Bumper).Select(i => _db.GetEntry(i.Id)!).ToList();
        var recentPlays = new Dictionary<long, DateTimeOffset>(); // this build's own airings
        TimeSpan cooldown = TimeSpan.FromMinutes(_channel.Ads.CooldownMinutes);

        List<SlotConfig> slots = _channel.Schedule.OrderBy(s => s.StartTime).ToList();
        DateTimeOffset cursor = from;
        int idCounter = 0;

        while (cursor < until)
        {
            (SlotConfig slot, DateTimeOffset nextSlotStart) = ActiveSlot(slots, cursor);
            CatalogEntry? episode = _picker.Next(slot.Show, _channel.Number, slot.OrderMode);

            if (episode is null)
            {
                warnings.Add($"No episodes found for show '{slot.Show}'. Slate until {nextSlotStart:HH:mm}.");
                DateTimeOffset slateEnd = nextSlotStart > cursor ? nextSlotStart : cursor.AddMinutes(30);
                items.Add(SpikeBuilder.Slate($"slate-{++idCounter}", cursor, (long)(slateEnd - cursor).TotalMilliseconds));
                programmes.Add(new GuideProgramme { Start = cursor, Stop = slateEnd, Title = $"{slot.Show} (no episodes)" });
                cursor = slateEnd;
                continue;
            }

            // Ad candidates: in the pool, era-matched if the pool asks, and outside the cooldown window.
            var ctx = new QueryContext(episode.Episode?.AirDate?.Year ?? episode.Item.Year);
            List<FillerClip> ads = pool.Filter(adLibrary, ctx)
                .Where(e => !OnCooldown(e.Item.Id, cursor, cooldown, recentPlays))
                .Select(e => new FillerClip(e.Item.Path, e.Item.DurationMs))
                .ToList();
            if (ads.Count < 4 && adLibrary.Count > 0)
                warnings.Add($"{cursor:ddd HH:mm} {slot.Show}: only {ads.Count} ads available after pool filter and cooldown.");

            var request = new SpikeRequest
            {
                Episode = new MediaProbe
                {
                    Path = episode.Item.Path,
                    DurationMs = episode.Item.DurationMs,
                    Chapters = episode.Chapters.Select(c => new Chapter(c.StartMs, c.EndMs, c.Title)).ToList(),
                    Streams = [],
                },
                Title = GuideTitle(episode),
                Start = cursor,
                Ads = ads,
                BreakPolicy = slot.BreakPolicy(_channel.Defaults),
                PaddingPolicy = slot.PaddingPolicy(_channel.Defaults),
                MaxRepeatsPerBreak = _channel.Ads.MaxRepeatsPerBreak,
                Bumpers = bumperSet,
            };
            SpikeResult built = SpikeBuilder.Build(request);
            warnings.AddRange(built.Warnings.Select(w => $"{cursor:ddd HH:mm} {slot.Show}: {w}"));

            DateTimeOffset slotEnd = built.Programme.Stop;
            if (slotEnd > nextSlotStart.AddMinutes(1) && nextSlotStart > cursor)
                warnings.Add($"{cursor:ddd HH:mm} {slot.Show} runs {(slotEnd - nextSlotStart).TotalMinutes:0} min past the next slot's start.");

            foreach (PlayoutItem item in built.Playout.Items)
            {
                string prefix = item.Id.Split('-')[0];
                items.Add(item with { Id = $"{prefix}-{++idCounter}" });
                if (item.Source is LocalSource local && prefix is "ad" or "bumper")
                {
                    long? clipId = (prefix == "ad" ? adLibrary : bumperLibrary).FirstOrDefault(e => e.Item.Path == local.Path)?.Item.Id;
                    if (clipId is { } id)
                    {
                        plays.Add(new PlayRecord(id, item.Start, $"{prefix} in {slot.Show}"));
                        if (prefix == "ad") recentPlays[id] = item.Start;
                    }
                }
            }
            plays.Add(new PlayRecord(episode.Item.Id, cursor, $"slot {slot.Time} {slot.Show}"));
            programmes.Add(built.Programme with
            {
                SubTitle = episode.Episode?.EpisodeTitle,
                EpisodeNumber = episode.Episode is { Season: { } s, Episode: { } e } ? $"S{s:00}E{e:00}" : null,
            });

            _log?.Invoke($"{cursor:ddd HH:mm}-{slotEnd:HH:mm}  {GuideTitle(episode)}  ({built.Acts.Count} acts, {built.Playout.Items.Count(i => i.Id.StartsWith("ad-"))} ads, {built.Playout.Items.Count(i => i.Id.StartsWith("bumper-"))} bumpers, pad {built.PadMs / 1000}s)");
            cursor = slotEnd;
        }

        return new BuildResult
        {
            Channel = _channel.Number,
            Start = from,
            End = cursor,
            Playout = new Playout.Playout { Version = Playout.Playout.VersionUri(_schemaVersion), Items = items },
            Programmes = programmes,
            Plays = plays,
            Warnings = warnings,
        };
    }

    private BumperSet LoadBumpers()
    {
        BumpersConfig cfg = _channel.Bumpers;
        if (string.IsNullOrWhiteSpace(cfg.Pool) || (!cfg.BeforeBreak && !cfg.AfterBreak)) return new BumperSet();

        PoolQuery pool = PoolQuery.Parse(cfg.Pool);
        List<CatalogEntry> all = pool.Filter(_db.ListItems(MediaKind.Bumper).Select(i => _db.GetEntry(i.Id)!)).ToList();
        if (all.Count == 0) return new BumperSet();

        List<FillerClip> Pick(bool enabled, string? tag) => !enabled ? [] : all
            .Where(e => tag is null || e.Tags.Any(t => t.Name == CatalogDb.NormalizeTag(tag)))
            .Select(e => new FillerClip(e.Item.Path, e.Item.DurationMs))
            .ToList();

        return new BumperSet
        {
            Before = Pick(cfg.BeforeBreak, cfg.BeforeTag),
            After = Pick(cfg.AfterBreak, cfg.AfterTag),
            MinAdMs = cfg.MinAdSeconds * 1000L,
        };
    }

    private bool OnCooldown(long itemId, DateTimeOffset at, TimeSpan cooldown, Dictionary<long, DateTimeOffset> recent)
    {
        if (recent.TryGetValue(itemId, out DateTimeOffset r) && at - r < cooldown) return true;
        DateTimeOffset? last = _db.LastPlayed(itemId, _channel.Number);
        return last is { } l && at - l < cooldown && at >= l;
    }

    /// <summary>The slot whose start is the latest at or before the cursor, and when the following slot begins.</summary>
    private static (SlotConfig Slot, DateTimeOffset NextStart) ActiveSlot(List<SlotConfig> slots, DateTimeOffset cursor)
    {
        TimeOnly now = TimeOnly.FromTimeSpan(cursor.TimeOfDay);
        DateTimeOffset midnight = new(cursor.Date, cursor.Offset);

        int index = -1;
        for (int i = 0; i < slots.Count; i++)
            if (slots[i].StartTime <= now) index = i;

        if (index < 0)
        {
            // Before the first slot of the day: yesterday's last slot is still running.
            index = slots.Count - 1;
            DateTimeOffset first = midnight.Add(slots[0].StartTime.ToTimeSpan());
            return (slots[index], first);
        }

        DateTimeOffset nextStart = index + 1 < slots.Count
            ? midnight.Add(slots[index + 1].StartTime.ToTimeSpan())
            : midnight.AddDays(1).Add(slots[0].StartTime.ToTimeSpan());
        return (slots[index], nextStart);
    }

    private static string GuideTitle(CatalogEntry e) => e.Episode?.Show ?? e.Item.Title ?? Path.GetFileNameWithoutExtension(e.Item.Path);
}
