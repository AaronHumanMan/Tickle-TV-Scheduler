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

    /// <summary>
    /// Episodes a previous build of this window already assigned, per show, in air order. A rebuild consumes
    /// these before asking the picker, so re-running a build does not move every show forward.
    /// </summary>
    public IReadOnlyDictionary<string, Queue<long>> PriorAssignments { get; init; } = new Dictionary<string, Queue<long>>();

    /// <summary>Parses the show out of a slot play's context, "slot HH:mm Show Name".</summary>
    public static string? ShowFromSlotContext(string context)
    {
        if (!context.StartsWith("slot ", StringComparison.Ordinal)) return null;
        int secondSpace = context.IndexOf(' ', 5);
        return secondSpace < 0 ? null : context[(secondSpace + 1)..];
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
        List<CatalogEntry> gapLibrary = _channel.Gap.HoldGrid
            ? PoolQuery.Parse(_channel.Gap.Pool).Filter(_db.ListItems(MediaKind.Commercial).Concat(_db.ListItems(MediaKind.Bumper)).Select(i => _db.GetEntry(i.Id)!)).ToList()
            : [];
        FillerClip? gapFiller = LoadFiller();
        if (gapFiller is null) warnings.Add("No filler clip matched the filler pool; gaps will be black slate.");
        List<CatalogEntry> bumperLibrary = _db.ListItems(MediaKind.Bumper).Select(i => _db.GetEntry(i.Id)!).ToList();
        var recentPlays = new Dictionary<long, DateTimeOffset>(); // this build's own airings
        TimeSpan cooldown = TimeSpan.FromMinutes(_channel.Ads.CooldownMinutes);

        List<SlotConfig> slots = _channel.Schedule.OrderBy(s => s.StartTime).ToList();
        DateTimeOffset cursor = from;
        int idCounter = 0;

        while (cursor < until)
        {
            (SlotConfig slot, DateTimeOffset nextSlotStart) = ActiveSlot(slots, cursor);

            // Hold the grid: a short stretch before the next slot that no episode fits into becomes a station break.
            long untilNextMs = (long)(nextSlotStart - cursor).TotalMilliseconds;
            if (_channel.Gap.HoldGrid && untilNextMs > 0 && untilNextMs <= _channel.Gap.MaxMinutes * 60_000L
                && !NextEpisodeFits(slot.Show, untilNextMs))
            {
                cursor = EmitStationBreak(items, programmes, plays, warnings, cursor, untilNextMs, gapLibrary, bumperSet, gapFiller, recentPlays, cooldown, ref idCounter, slot.Show);
                continue;
            }

            CatalogEntry? episode = ReusePrior(slot.Show) ?? _picker.Next(slot.Show, _channel.Number, slot.OrderMode);

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

            var probe = new MediaProbe
            {
                Path = episode.Item.Path,
                DurationMs = episode.Item.DurationMs,
                Chapters = episode.Chapters.Select(c => new Chapter(c.StartMs, c.EndMs, c.Title)).ToList(),
                Streams = [],
            };
            BreakPolicy breakPolicy = slot.BreakPolicy(_channel.Defaults);
            PaddingPolicy paddingPolicy = slot.PaddingPolicy(_channel.Defaults);

            // No natural break points: run the show straight through and pad only at the end, to a short boundary.
            if (BreakPlanner.PlanActs(probe.DurationMs, probe.Chapters, breakPolicy).Count <= 1)
            {
                breakPolicy = breakPolicy with { PreRoll = false, PostRoll = true };
                paddingPolicy = paddingPolicy with { Mode = _channel.Defaults.NoChapterPaddingMode };
            }

            // Hold the grid: never pad past the next slot's start when the content itself fits before it.
            DateTimeOffset? slotEndOverride = null;
            if (_channel.Gap.HoldGrid && nextSlotStart > cursor)
            {
                DateTimeOffset natural = Padding.SlotEnd(cursor, probe.DurationMs, paddingPolicy);
                if (natural > nextSlotStart && cursor.AddMilliseconds(probe.DurationMs) <= nextSlotStart)
                    slotEndOverride = nextSlotStart;
            }

            var request = new SpikeRequest
            {
                Episode = probe,
                Title = GuideTitle(episode),
                Start = cursor,
                Ads = ads,
                BreakPolicy = breakPolicy,
                PaddingPolicy = paddingPolicy,
                SlotEnd = slotEndOverride,
                MaxRepeatsPerBreak = _channel.Ads.MaxRepeatsPerBreak,
                Bumpers = bumperSet,
                Filler = gapFiller,
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

    /// <summary>Would the show's next episode (without consuming it) finish before the next slot starts?</summary>
    private bool NextEpisodeFits(string show, long untilNextMs)
    {
        long? duration = PeekPriorDuration(show) ?? _picker.PeekDuration(show, _channel.Number);
        return duration is null || duration.Value <= untilNextMs;
    }

    private long? PeekPriorDuration(string show)
    {
        var match = PriorAssignments.FirstOrDefault(kv => string.Equals(kv.Key, show, StringComparison.OrdinalIgnoreCase));
        if (match.Value is null || match.Value.Count == 0) return null;
        return _db.FindById(match.Value.Peek())?.DurationMs;
    }

    private DateTimeOffset EmitStationBreak(
        List<PlayoutItem> items, List<GuideProgramme> programmes, List<PlayRecord> plays, List<string> warnings,
        DateTimeOffset start, long durationMs, List<CatalogEntry> gapLibrary, BumperSet bumpers, FillerClip? filler,
        Dictionary<long, DateTimeOffset> recentPlays, TimeSpan cooldown, ref int idCounter, string nextShow)
    {
        var clips = gapLibrary
            .Where(e => !OnCooldown(e.Item.Id, start, cooldown, recentPlays))
            .Select(e => new FillerClip(e.Item.Path, e.Item.DurationMs))
            .ToList();
        (var gapItems, var gapWarnings) = SpikeBuilder.FillGap(start, durationMs, clips, bumpers, filler, _channel.Ads.MaxRepeatsPerBreak);
        warnings.AddRange(gapWarnings.Select(w => $"{start:ddd HH:mm} station break: {w}"));

        foreach (PlayoutItem item in gapItems)
        {
            string prefix = item.Id.Split('-')[0];
            items.Add(item with { Id = $"{prefix}-{++idCounter}" });
            if (item.Source is LocalSource local && prefix is "ad" or "bumper")
            {
                long? clipId = gapLibrary.FirstOrDefault(e => e.Item.Path == local.Path)?.Item.Id;
                if (clipId is { } id)
                {
                    plays.Add(new PlayRecord(id, item.Start, "station break"));
                    recentPlays[id] = item.Start;
                }
            }
        }

        DateTimeOffset end = start.AddMilliseconds(durationMs);
        // The guide shows the gap as part of whatever just ended; a cold start gets its own entry.
        if (programmes.Count > 0 && programmes[^1].Stop == start)
            programmes[^1] = programmes[^1] with { Stop = end };
        else
            programmes.Add(new GuideProgramme { Start = start, Stop = end, Title = "Station break" });

        _log?.Invoke($"{start:ddd HH:mm}-{end:HH:mm}  Station break before {nextShow}  ({gapItems.Count} items)");
        return end;
    }

    private CatalogEntry? ReusePrior(string show)
    {
        var match = PriorAssignments.FirstOrDefault(kv => string.Equals(kv.Key, show, StringComparison.OrdinalIgnoreCase));
        if (match.Value is null) return null;
        while (match.Value.Count > 0)
        {
            long id = match.Value.Dequeue();
            CatalogEntry? entry = _db.GetEntry(id);
            if (entry is not null && entry.Item.MissingUtc is null) return entry;
        }
        return null;
    }

    private FillerClip? LoadFiller()
    {
        if (string.IsNullOrWhiteSpace(_channel.Filler.Pool)) return null;
        PoolQuery pool = PoolQuery.Parse(_channel.Filler.Pool);
        CatalogEntry? pick = pool.Filter(_db.ListItems(MediaKind.Filler).Select(i => _db.GetEntry(i.Id)!)).FirstOrDefault();
        return pick is null ? null : new FillerClip(pick.Item.Path, pick.Item.DurationMs);
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
