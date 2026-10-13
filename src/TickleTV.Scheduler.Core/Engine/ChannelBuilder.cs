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
/// Turns a channel's daily grid into a playout window. Each date follows the day plan the channel's calendar
/// picks for it; a plan takes over at its own first slot, and the previous day's last slot runs until then.
/// Each slot airs episodes of its show back to back, each padded to its boundary, until the next slot's start time. A slot that lists several shows is a block:
/// the shows take turns, one episode each, down the list or at random, and with hold-the-grid a show whose
/// next episode would not fit before the next slot gives its turn to one that fits. Ads come from the channel's pool with a cooldown so
/// the same clip does not return too soon.
/// </summary>
public sealed class ChannelBuilder
{
    private readonly CatalogDb _db;
    private readonly EpisodePicker _picker;
    private readonly ChannelConfig _channel;
    private readonly string _schemaVersion;
    private readonly Action<string>? _log;
    private readonly Random _random;
    /// <summary>The show that played last in each block (index into the slot's show list; -1 when none has yet).</summary>
    private readonly Dictionary<SlotConfig, int> _lastTurn = new();
    private readonly Dictionary<DayPlan, List<SlotConfig>> _sorted = new();
    /// <summary>Where the build has got to, for choices that depend on the date (the holiday rotation is per year).</summary>
    private DateTimeOffset _cursor;

    public ChannelBuilder(CatalogDb db, EpisodePicker picker, ChannelConfig channel, string schemaVersion, Action<string>? log = null, int? seed = null)
    {
        _db = db;
        _picker = picker;
        _channel = channel;
        _schemaVersion = schemaVersion;
        _log = log;
        _random = seed is null ? Random.Shared : new Random(seed.Value);
    }

    /// <summary>
    /// Episodes a previous build of this window already assigned, per show, in air order. A rebuild consumes
    /// these before asking the picker, so re-running a build does not move every show forward.
    /// </summary>
    public IReadOnlyDictionary<string, Queue<long>> PriorAssignments { get; init; } = new Dictionary<string, Queue<long>>();

    /// <summary>
    /// The time zone the schedule's clock times are read in. Slot times, padding boundaries and the calendar all
    /// follow this zone's wall clock, so a build that crosses a daylight-saving change keeps 8pm at 8pm. Null means a
    /// fixed offset taken from the build's start, which never changes mid-build (fine for tests, wrong for a real zone).
    /// </summary>
    public TimeZoneInfo? TimeZone { get; init; }

    private TimeZoneInfo _zone = TimeZoneInfo.Utc;

    /// <summary>The same instant, with the offset the zone uses at that instant.</summary>
    private DateTimeOffset InZone(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, _zone);

    /// <summary>A clock time on a date, with the offset the zone uses then. A time the clocks skip takes the offset before the change.</summary>
    private DateTimeOffset At(DateOnly date, TimeOnly time)
    {
        var local = new DateTime(date, time, DateTimeKind.Unspecified);
        TimeSpan offset = _zone.IsInvalidTime(local) ? _zone.GetUtcOffset(local.AddHours(-1)) : _zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>Parses the show out of a slot play's context, "slot HH:mm Show Name".</summary>
    public static string? ShowFromSlotContext(string context)
    {
        if (!context.StartsWith("slot ", StringComparison.Ordinal)) return null;
        int secondSpace = context.IndexOf(' ', 5);
        return secondSpace < 0 ? null : context[(secondSpace + 1)..];
    }

    public BuildResult Build(DateTimeOffset from, DateTimeOffset until)
    {
        _zone = TimeZone ?? TimeZoneInfo.CreateCustomTimeZone($"fixed{from.Offset:hhmm}", from.Offset, "Fixed offset", "Fixed offset");
        from = InZone(from);
        var items = new List<PlayoutItem>();
        var programmes = new List<GuideProgramme>();
        var plays = new List<PlayRecord>();
        var warnings = new List<string>();

        PoolQuery pool = PoolQuery.Parse(_channel.Ads.Pool);
        List<CatalogEntry> adLibrary = _db.ListItems(MediaKind.Commercial)
            .Concat(_db.ListItems(MediaKind.Unknown))
            .Select(i => _db.GetEntry(i.Id)!)
            .ToList();
        // The channel's never-list applies everywhere; each slot narrows further, see SlotLibrary.
        List<CatalogEntry> channelLibrary = adLibrary.Where(e => AdRules.Allowed(e, _channel.Ads, null)).ToList();
        var slotLibraries = new Dictionary<SlotConfig, (List<CatalogEntry> Strict, List<CatalogEntry> Loose)>();
        BumperSet bumperSet = LoadBumpers();
        List<CatalogEntry> gapLibrary = _channel.Gap.HoldGrid
            ? PoolQuery.Parse(_channel.Gap.Pool).Filter(_db.ListItems(MediaKind.Commercial).Concat(_db.ListItems(MediaKind.Bumper)).Select(i => _db.GetEntry(i.Id)!)).ToList()
            : [];
        FillerClip? gapFiller = LoadFiller();
        if (gapFiller is null) warnings.Add("No filler clip matched the filler pool; gaps will be black slate.");
        List<CatalogEntry> bumperLibrary = _db.ListItems(MediaKind.Bumper).Select(i => _db.GetEntry(i.Id)!).ToList();
        var recentPlays = new Dictionary<long, DateTimeOffset>(); // this build's own airings
        TimeSpan cooldown = TimeSpan.FromMinutes(_channel.Ads.CooldownMinutes);

        bool manyPlans = _channel.Plans.Count > 1;
        foreach (DayPlan plan in _channel.Plans)
        {
            foreach (SlotConfig block in plan.Slots.Where(s => s.ShowList.Count > 1))
            {
                // Blocks carry on from wherever they left off: in order, the show after the one that aired last;
                // at random, anything but the one that aired last.
                string? last = _db.LastSlotShow(_channel.Number, block.Time, from);
                _lastTurn[block] = last is null ? -1 : IndexOfShow(block, last);
                if (BlockAdvisor.Advice(_channel, block, _db.AverageEpisodeMs) is { } advice)
                    warnings.Add($"{(manyPlans ? plan.Name + " " : "")}{block.Time} block: {advice}");
            }
        }
        DateTimeOffset cursor = from;
        int idCounter = 0;
        DayPlan? currentPlan = null;

        while (cursor < until)
        {
            // Re-read the offset each time round: the clocks may have changed since the last programme.
            cursor = InZone(cursor);
            (SlotConfig slot, DateTimeOffset nextSlotStart, DayPlan plan) = ActiveSlot(cursor);
            if (manyPlans && !ReferenceEquals(plan, currentPlan))
            {
                _log?.Invoke($"-- {cursor:ddd MMM d}: following the \"{plan.Name}\" day plan");
                currentPlan = plan;
            }

            // Hold the grid: a short stretch before the next slot that no episode fits into becomes a station break.
            long untilNextMs = (long)(nextSlotStart - cursor).TotalMilliseconds;
            _cursor = cursor;
            string? show = ChooseShow(slot, untilNextMs);
            if (show is null)
            {
                var gapClips = gapLibrary.Where(e => AdRules.Allowed(e, _channel.Ads, slot.Ads)).ToList();
                (HolidayAdsMode gapHolidayMode, string? gapHoliday) = HolidayAdsFor(slot, cursor);
                HashSet<string>? gapPreferred = null;
                if (gapHoliday is not null)
                {
                    var tagged = gapClips.Where(e => AdRules.IsHolidayAd(e, gapHoliday)).ToList();
                    if (gapHolidayMode == HolidayAdsMode.Only && tagged.Count >= AdRules.FallbackBelow) gapClips = tagged;
                    else if (gapHolidayMode == HolidayAdsMode.Mix) gapPreferred = tagged.Select(e => e.Item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                cursor = EmitStationBreak(items, programmes, plays, warnings, cursor, untilNextMs, gapClips, bumperSet, gapFiller, recentPlays, cooldown, ref idCounter, slot.Label, gapPreferred);
                continue;
            }

            CatalogEntry? episode = PickEpisode(slot, show, cursor);
            // A show with no episodes hands its turn to another show in the block.
            var tried = new HashSet<int> { IndexOfShow(slot, show) };
            while (episode is null && tried.Count < slot.ShowList.Count)
            {
                warnings.Add($"No {EpisodeWords(slot)} found for show '{show}'; skipping its turn.");
                show = TakeTurn(slot, TurnOrder(slot).First(i => !tried.Contains(i)));
                tried.Add(IndexOfShow(slot, show));
                episode = PickEpisode(slot, show, cursor);
            }

            if (episode is null)
            {
                warnings.Add($"No {EpisodeWords(slot)} found for show '{show}'. Slate until {nextSlotStart:HH:mm}.");
                DateTimeOffset slateEnd = nextSlotStart > cursor ? nextSlotStart : cursor.AddMinutes(30);
                items.Add(SpikeBuilder.Slate($"slate-{++idCounter}", cursor, (long)(slateEnd - cursor).TotalMilliseconds));
                programmes.Add(new GuideProgramme { Start = cursor, Stop = slateEnd, Title = $"{slot.Label} (no episodes)" });
                cursor = slateEnd;
                continue;
            }

            // Ad candidates: allowed by the slot's category and decade rules, from around the episode's year if the slot
            // asks, in the pool (era-matched if the pool asks), and outside the cooldown window.
            int? episodeYear = episode.Episode?.AirDate?.Year ?? episode.Item.Year;
            var ctx = new QueryContext(episodeYear);
            (List<CatalogEntry> strictLibrary, List<CatalogEntry> looseLibrary) = SlotLibrary(slot, channelLibrary, slotLibraries, warnings);
            List<CatalogEntry> eraLibrary = strictLibrary;
            if (slot.Ads is { EraMode: EraMode.Show } eraRule)
            {
                if (episodeYear is null)
                {
                    string note = $"{slot.Time} slot ({slot.Label}): {show} episodes have no year or air date, so \"commercials from around when the episode aired\" can't apply to them; commercials from any year air.";
                    if (!warnings.Contains(note)) warnings.Add(note);
                }
                else eraLibrary = strictLibrary.Where(e => AdRules.InShowEra(e, eraRule, episodeYear)).ToList();
            }
            // Holiday rule: "only" narrows the library to the holiday's commercials (falling back when they run short);
            // "mix" hands them to the break filler as favourites, so about half of each break is seasonal.
            (HolidayAdsMode holidayMode, string? holidayKey) = HolidayAdsFor(slot, cursor);
            HashSet<string>? preferredAds = null;
            List<CatalogEntry> holidayLibrary = eraLibrary;
            if (holidayKey is not null)
            {
                var tagged = eraLibrary.Where(e => AdRules.IsHolidayAd(e, holidayKey)).ToList();
                if (holidayMode == HolidayAdsMode.Only) holidayLibrary = tagged;
                else preferredAds = tagged.Select(e => e.Item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            List<FillerClip> ads = Candidates(pool, holidayLibrary, ctx, cursor, cooldown, recentPlays);
            if (ads.Count < AdRules.FallbackBelow && !ReferenceEquals(holidayLibrary, eraLibrary))
            {
                // Too few of the holiday's commercials are free: let the slot's other commercials in.
                List<FillerClip> fallback = Candidates(pool, eraLibrary, ctx, cursor, cooldown, recentPlays);
                if (fallback.Count > ads.Count)
                {
                    warnings.Add($"{cursor:ddd HH:mm} {show}: only {ads.Count} {HolidayTagger.Name(holidayKey!)} commercials were free, so other commercials may air in its breaks.");
                    ads = fallback;
                }
            }
            if (ads.Count < AdRules.FallbackBelow && !ReferenceEquals(eraLibrary, strictLibrary))
            {
                // Too few commercials from the episode's years: widen to any year the slot's categories allow.
                List<FillerClip> fallback = Candidates(pool, strictLibrary, ctx, cursor, cooldown, recentPlays);
                if (fallback.Count > ads.Count)
                {
                    warnings.Add($"{cursor:ddd HH:mm} {show}: only {ads.Count} commercials from around {episodeYear} were free, so commercials from any year may air in its breaks.");
                    ads = fallback;
                }
            }
            if (ads.Count < AdRules.FallbackBelow && !ReferenceEquals(strictLibrary, looseLibrary))
            {
                // The slot's categories or decades have run short: let any commercial in (the never-lists still hold) so the show still ends on time.
                List<FillerClip> fallback = Candidates(pool, looseLibrary, ctx, cursor, cooldown, recentPlays);
                if (fallback.Count > ads.Count)
                {
                    warnings.Add($"{cursor:ddd HH:mm} {show}: only {ads.Count} commercials that fit the slot's rules were free, so any commercial may air in its breaks.");
                    ads = fallback;
                }
            }
            if (ads.Count < AdRules.FallbackBelow && adLibrary.Count > 0)
                warnings.Add($"{cursor:ddd HH:mm} {show}: only {ads.Count} ads available after pool filter and cooldown.");

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
                Random = _random,
                PreferredAds = preferredAds,
                Bumpers = bumperSet,
                Filler = gapFiller,
            };
            SpikeResult built = SpikeBuilder.Build(request);
            warnings.AddRange(built.Warnings.Select(w => $"{cursor:ddd HH:mm} {show}: {w}"));

            DateTimeOffset slotEnd = built.Programme.Stop;
            if (slotEnd > nextSlotStart.AddMinutes(1) && nextSlotStart > cursor)
                warnings.Add($"{cursor:ddd HH:mm} {show} runs {(slotEnd - nextSlotStart).TotalMinutes:0} min past the next slot's start.");

            foreach (PlayoutItem item in built.Playout.Items)
            {
                string prefix = item.Id.Split('-')[0];
                items.Add(item with { Id = $"{prefix}-{++idCounter}" });
                if (item.Source is LocalSource local && prefix is "ad" or "bumper")
                {
                    long? clipId = (prefix == "ad" ? adLibrary : bumperLibrary).FirstOrDefault(e => e.Item.Path == local.Path)?.Item.Id;
                    if (clipId is { } id)
                    {
                        plays.Add(new PlayRecord(id, item.Start, $"{prefix} in {show}"));
                        if (prefix == "ad") recentPlays[id] = item.Start;
                    }
                }
            }
            plays.Add(new PlayRecord(episode.Item.Id, cursor, $"slot {slot.Time} {show}"));
            programmes.Add(built.Programme with
            {
                SubTitle = episode.Episode?.EpisodeTitle,
                Description = episode.Episode?.Plot,
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

    /// <summary>
    /// Which show plays next in the slot. A single show always plays. In a block the shows take turns, down the
    /// list or at random; with hold-the-grid the turn passes to the first show in that order whose next episode
    /// fits before the next slot. Null means nothing fits and the leftover is short enough to fill with a station
    /// break instead.
    /// </summary>
    private string? ChooseShow(SlotConfig slot, long untilNextMs)
    {
        IReadOnlyList<string> shows = slot.ShowList;
        List<int> order = TurnOrder(slot);
        bool holding = _channel.Gap.HoldGrid && untilNextMs > 0;
        if (holding)
        {
            foreach (int idx in order)
                if (NextEpisodeFits(slot, shows[idx], _cursor, untilNextMs)) return TakeTurn(slot, idx);
            if (untilNextMs <= _channel.Gap.MaxMinutes * 60_000L) return null;
        }
        return TakeTurn(slot, order[0]);
    }

    /// <summary>
    /// The block's shows in the order they would like to play next. In order: the show after the last one, then
    /// round the list. Random: shuffled, with the show that just played moved to the back so it never plays twice
    /// in a row unless nothing else will do.
    /// </summary>
    private List<int> TurnOrder(SlotConfig slot)
    {
        int count = slot.ShowList.Count;
        int last = _lastTurn.GetValueOrDefault(slot, -1);
        if (!slot.RandomTurns || count < 2)
            return Enumerable.Range(1, count).Select(i => (last + i) % count).ToList();

        var order = Enumerable.Range(0, count).ToList();
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        if (last >= 0 && order.Remove(last)) order.Add(last);
        return order;
    }

    private string TakeTurn(SlotConfig slot, int index)
    {
        _lastTurn[slot] = index;
        return slot.ShowList[index];
    }

    private static int IndexOfShow(SlotConfig slot, string show)
    {
        for (int i = 0; i < slot.ShowList.Count; i++)
            if (string.Equals(slot.ShowList[i], show, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Would the show's next episode (without consuming it) finish before the next slot starts?</summary>
    private bool NextEpisodeFits(SlotConfig slot, string show, DateTimeOffset at, long untilNextMs)
    {
        long? duration = PeekPriorDuration(show);
        if (duration is null && slot.HolidayMode != HolidayMode.Any && slot.Holiday is { } holiday)
            duration = _picker.PeekHolidayDuration(show, _channel.Number, holiday, at.Year, slot.HolidayMode);
        if (duration is null && slot.HolidayMode != HolidayMode.Only)
            duration ??= _picker.PeekDuration(show, _channel.Number);
        return duration is null || duration.Value <= untilNextMs;
    }

    /// <summary>
    /// The next episode for a show in this slot: an earlier build's assignment first, then the slot's holiday
    /// choice (its own rotation), then the regular rotation unless the slot wants holiday episodes only.
    /// </summary>
    private CatalogEntry? PickEpisode(SlotConfig slot, string show, DateTimeOffset at)
    {
        if (ReusePrior(show) is { } prior) return prior;
        if (slot.HolidayMode != HolidayMode.Any && slot.Holiday is { } holiday)
        {
            CatalogEntry? festive = _picker.NextHoliday(show, _channel.Number, holiday, at.Year, slot.OrderMode, slot.HolidayMode);
            if (festive is not null) return festive;
            if (slot.HolidayMode == HolidayMode.Only) return null;
        }
        return _picker.Next(show, _channel.Number, slot.OrderMode);
    }

    private static string EpisodeWords(SlotConfig slot) =>
        slot.HolidayMode == HolidayMode.Only && slot.Holiday is { } h ? $"{Holidays.Find(h)?.Name ?? h} episodes" : "episodes";

    private long? PeekPriorDuration(string show)
    {
        var match = PriorAssignments.FirstOrDefault(kv => string.Equals(kv.Key, show, StringComparison.OrdinalIgnoreCase));
        if (match.Value is null || match.Value.Count == 0) return null;
        return _db.FindById(match.Value.Peek())?.DurationMs;
    }

    /// <summary>The commercials a slot may use: strict (its only-list, decades and never-lists) and loose (never-lists only, for the fallback). Warns once when the strict set is small. The show-era rule is applied per episode by the caller.</summary>
    private (List<CatalogEntry> Strict, List<CatalogEntry> Loose) SlotLibrary(
        SlotConfig slot, List<CatalogEntry> channelLibrary, Dictionary<SlotConfig, (List<CatalogEntry>, List<CatalogEntry>)> cache, List<string> warnings)
    {
        if (cache.TryGetValue(slot, out var cached)) return cached;
        if (slot.Ads is null || slot.Ads.IsEmpty)
        {
            cache[slot] = (channelLibrary, channelLibrary);
            return cache[slot];
        }
        List<CatalogEntry> strict = channelLibrary.Where(e => AdRules.Allowed(e, _channel.Ads, slot.Ads)).ToList();
        bool narrows = slot.Ads.Only.Count > 0 || slot.Ads.EraMode == EraMode.Decades;
        List<CatalogEntry> loose = narrows ? channelLibrary.Where(e => AdRules.Allowed(e, _channel.Ads, slot.Ads, strict: false)).ToList() : strict;
        if (narrows && strict.Count < AdRules.SmallPool)
            warnings.Add($"{slot.Time} slot ({slot.Label}): only {strict.Count} commercials match its rules ({AdRules.Describe(slot.Ads)}). They will repeat a lot, and breaks fall back to any commercial when they run out.");
        cache[slot] = (strict, loose);
        return cache[slot];
    }

    private List<FillerClip> Candidates(PoolQuery pool, List<CatalogEntry> library, QueryContext ctx, DateTimeOffset cursor, TimeSpan cooldown, Dictionary<long, DateTimeOffset> recentPlays) =>
        pool.Filter(library, ctx)
            .Where(e => !OnCooldown(e.Item.Id, cursor, cooldown, recentPlays))
            .Select(e => new FillerClip(e.Item.Path, e.Item.DurationMs))
            .ToList();

    private DateTimeOffset EmitStationBreak(
        List<PlayoutItem> items, List<GuideProgramme> programmes, List<PlayRecord> plays, List<string> warnings,
        DateTimeOffset start, long durationMs, List<CatalogEntry> gapLibrary, BumperSet bumpers, FillerClip? filler,
        Dictionary<long, DateTimeOffset> recentPlays, TimeSpan cooldown, ref int idCounter, string nextShow, IReadOnlySet<string>? preferred = null)
    {
        var clips = gapLibrary
            .Where(e => !OnCooldown(e.Item.Id, start, cooldown, recentPlays))
            .Select(e => new FillerClip(e.Item.Path, e.Item.DurationMs))
            .ToList();
        (var gapItems, var gapWarnings) = SpikeBuilder.FillGap(start, durationMs, clips, bumpers, filler, _channel.Ads.MaxRepeatsPerBreak, _random, preferred);
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
            Placement = cfg.PlacementMode,
        };
    }

    private bool OnCooldown(long itemId, DateTimeOffset at, TimeSpan cooldown, Dictionary<long, DateTimeOffset> recent)
    {
        if (recent.TryGetValue(itemId, out DateTimeOffset r) && at - r < cooldown) return true;
        DateTimeOffset? last = _db.LastPlayed(itemId, _channel.Number);
        return last is { } l && at - l < cooldown && at >= l;
    }

    /// <summary>The slot whose start is the latest at or before the cursor, and when the following slot begins.</summary>
    /// <summary>
    /// The slot on air at <paramref name="cursor"/>, when the next one starts, and the day plan in force. A
    /// broadcast day begins at its plan's first slot; before that, the previous day's plan is still running its
    /// last slot.
    /// </summary>
    private (SlotConfig Slot, DateTimeOffset NextStart, DayPlan Plan) ActiveSlot(DateTimeOffset cursor)
    {
        cursor = InZone(cursor);
        DateOnly date = DateOnly.FromDateTime(cursor.DateTime);
        DayPlan plan = PlanCalendar.PlanFor(_channel, date);
        List<SlotConfig> slots = Sorted(plan);
        if (cursor < At(date, slots[0].StartTime))
        {
            date = date.AddDays(-1);
            plan = PlanCalendar.PlanFor(_channel, date);
            slots = Sorted(plan);
        }

        int index = slots.Count - 1;
        for (int i = 0; i < slots.Count; i++)
            if (At(date, slots[i].StartTime) <= cursor) index = i;

        DateTimeOffset nextStart = index + 1 < slots.Count
            ? At(date, slots[index + 1].StartTime)
            : FirstSlotStart(date.AddDays(1));
        return (slots[index], nextStart, plan);
    }

    /// <summary>
    /// The holiday whose commercials this slot favours at this moment, and how. A slot set to follow the calendar takes
    /// the holiday of the rule that chose the day's plan, and favours nothing on an ordinary day.
    /// </summary>
    private (HolidayAdsMode Mode, string? Key) HolidayAdsFor(SlotConfig slot, DateTimeOffset at)
    {
        if (slot.Ads is null || slot.Ads.HolidayAdsMode == HolidayAdsMode.None || slot.Ads.Holiday is null) return (HolidayAdsMode.None, null);
        string? key = slot.Ads.Holiday;
        if (key == SlotAdsConfig.CalendarHoliday)
        {
            CalendarRule? rule = PlanCalendar.RuleFor(_channel, DateOnly.FromDateTime(InZone(at).DateTime));
            key = rule?.Kind == RuleKind.Holiday ? rule.Holiday : null;
        }
        return key is null ? (HolidayAdsMode.None, null) : (slot.Ads.HolidayAdsMode, HolidayTagger.Fold(key));
    }

    /// <summary>When the given date's plan takes over: its first slot that day.</summary>
    private DateTimeOffset FirstSlotStart(DateOnly date) =>
        At(date, Sorted(PlanCalendar.PlanFor(_channel, date))[0].StartTime);

    private List<SlotConfig> Sorted(DayPlan plan)
    {
        if (!_sorted.TryGetValue(plan, out List<SlotConfig>? slots))
        {
            slots = plan.Slots.OrderBy(s => s.StartTime).ToList();
            if (slots.Count == 0) throw new InvalidDataException($"The day plan '{plan.Name}' has no time slots.");
            _sorted[plan] = slots;
        }
        return slots;
    }

    private static string GuideTitle(CatalogEntry e) => e.Episode?.Show ?? e.Item.Title ?? Path.GetFileNameWithoutExtension(e.Item.Path);
}
