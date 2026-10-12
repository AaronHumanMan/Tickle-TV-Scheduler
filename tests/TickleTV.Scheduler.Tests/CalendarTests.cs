using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Tests;

/// <summary>Day plans and the calendar that picks one per date: config shapes, holidays, rule priority, and the builder switching plans.</summary>
public class CalendarTests : IDisposable
{
    private const long Min = 60_000;
    private readonly CatalogDb _db = CatalogDb.InMemory();
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Old_single_schedule_loads_as_the_default_plan_and_saves_as_plans()
    {
        string yaml = """
            next: { lineup: C:\\x\\lineup.json }
            channels:
              - number: "1"
                schedule:
                  - time: "06:00"
                    show: Cheers
            """;
        SchedulerConfig cfg = SchedulerConfig.Parse(yaml, Path.GetTempPath());
        ChannelConfig ch = cfg.Channels[0];
        Assert.Single(ch.Plans);
        Assert.Equal(DayPlan.DefaultName, ch.Default.Name);
        Assert.Equal(["Cheers"], ch.Schedule[0].ShowList);
        Assert.Null(ch.LegacySchedule);

        string path = Path.Combine(Path.GetTempPath(), "tickletv-plans-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            cfg.Save(path);
            string text = File.ReadAllText(path);
            Assert.Contains("plans:", text);
            Assert.DoesNotContain("schedule:", text);
            Assert.DoesNotContain("calendar:", text);
            Assert.Equal(["Cheers"], SchedulerConfig.Load(path).Channels[0].Schedule[0].ShowList);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Plans_and_calendar_round_trip_and_bad_rules_are_rejected()
    {
        string yaml = """
            next: { lineup: C:\\x\\lineup.json }
            channels:
              - number: "1"
                default_plan: Normal Day
                plans:
                  - name: Normal Day
                    slots:
                      - { time: "06:00", show: Cheers }
                  - name: Weekend
                    slots:
                      - { time: "08:00", show: Frasier }
                  - name: Christmas
                    slots:
                      - { time: "07:00", show: Wings }
                calendar:
                  - { plan: Weekend, days: [saturday, sunday] }
                  - { plan: Christmas, holiday: christmas, days_before: 6 }
                  - { plan: Weekend, from: "07-01", to: "07-07" }
                  - { plan: Christmas, dates: ["2026-10-31"] }
            """;
        SchedulerConfig cfg = SchedulerConfig.Parse(yaml, Path.GetTempPath());
        ChannelConfig ch = cfg.Channels[0];
        Assert.Equal(3, ch.Plans.Count);
        Assert.Equal("Normal Day", ch.Default.Name);
        Assert.Equal([RuleKind.Weekdays, RuleKind.Holiday, RuleKind.Range, RuleKind.Dates], ch.Calendar.Select(r => r.Kind).ToArray());

        Assert.Throws<InvalidDataException>(() => SchedulerConfig.Parse(yaml.Replace("plan: Weekend, days: [saturday, sunday]", "plan: Nowhere, days: [saturday]"), Path.GetTempPath()));
        Assert.Throws<InvalidDataException>(() => SchedulerConfig.Parse(yaml.Replace("holiday: christmas", "holiday: festivus"), Path.GetTempPath()));
        Assert.Throws<InvalidDataException>(() => SchedulerConfig.Parse(yaml.Replace("days: [saturday, sunday]", "days: [caturday]"), Path.GetTempPath()));
        Assert.Throws<InvalidDataException>(() => SchedulerConfig.Parse(yaml.Replace("default_plan: Normal Day", "default_plan: Missing"), Path.GetTempPath()));

        string path = Path.Combine(Path.GetTempPath(), "tickletv-cal-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            cfg.Save(path);
            SchedulerConfig back = SchedulerConfig.Load(path);
            Assert.Equal(4, back.Channels[0].Calendar.Count);
            Assert.Equal("Weekend", back.Channels[0].Calendar[0].Plan);
            Assert.Equal(6, back.Channels[0].Calendar[1].DaysBefore);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Holidays_land_on_the_right_dates()
    {
        Assert.Equal(new DateOnly(2026, 11, 26), Holidays.Find("thanksgiving")!.On(2026));
        Assert.Equal(new DateOnly(2027, 11, 25), Holidays.Find("thanksgiving")!.On(2027));
        Assert.Equal(new DateOnly(2026, 4, 5), Holidays.Easter(2026));
        Assert.Equal(new DateOnly(2027, 3, 28), Holidays.Easter(2027));
        Assert.Equal(new DateOnly(2024, 3, 31), Holidays.Easter(2024));
        Assert.Equal(new DateOnly(2026, 12, 25), Holidays.Find("Christmas")!.On(2026));
        Assert.Null(Holidays.Find("festivus"));
    }

    private static ChannelConfig ThreePlans()
    {
        var ch = new ChannelConfig
        {
            Number = "1",
            Plans =
            [
                new DayPlan { Name = "Normal Day", Slots = [new SlotConfig { Time = "06:00", Show = "Short" }] },
                new DayPlan { Name = "Weekend", Slots = [new SlotConfig { Time = "09:00", Show = "Doc" }] },
                new DayPlan { Name = "Christmas", Slots = [new SlotConfig { Time = "07:00", Show = "Doc" }] },
            ],
            Calendar =
            [
                new CalendarRule { Plan = "Weekend", Days = ["saturday", "sunday"] },
                new CalendarRule { Plan = "Christmas", Holiday = "christmas", DaysBefore = 6 },
                new CalendarRule { Plan = "Normal Day", Dates = ["2026-12-20"] },
            ],
        };
        return ch;
    }

    [Fact]
    public void Rules_are_checked_from_most_to_least_specific()
    {
        ChannelConfig ch = ThreePlans();
        Assert.Equal("Normal Day", PlanCalendar.PlanNameFor(ch, new DateOnly(2026, 10, 12))); // a Monday
        Assert.Equal("Weekend", PlanCalendar.PlanNameFor(ch, new DateOnly(2026, 10, 17)));    // a Saturday
        Assert.Equal("Christmas", PlanCalendar.PlanNameFor(ch, new DateOnly(2026, 12, 19)));  // Saturday in Christmas week: holiday beats weekday
        Assert.Equal("Christmas", PlanCalendar.PlanNameFor(ch, new DateOnly(2026, 12, 25)));
        Assert.Equal("Weekend", PlanCalendar.PlanNameFor(ch, new DateOnly(2026, 12, 26)));    // the day after is a Saturday: the week-before rule has stopped
        Assert.Equal("Normal Day", PlanCalendar.PlanNameFor(ch, new DateOnly(2026, 12, 28))); // the Monday after
        Assert.Equal("Normal Day", PlanCalendar.PlanNameFor(ch, new DateOnly(2026, 12, 20))); // pinned, beats the holiday
        Assert.Equal(RuleKind.Dates, PlanCalendar.RuleFor(ch, new DateOnly(2026, 12, 20))!.Kind);
        Assert.Null(PlanCalendar.RuleFor(ch, new DateOnly(2026, 10, 12)));

        // A range that wraps the year, and the shorter of two overlapping rules wins.
        ch.Calendar.Add(new CalendarRule { Plan = "Weekend", From = "12-20", To = "01-03" });
        Assert.Equal("Weekend", PlanCalendar.PlanNameFor(ch, new DateOnly(2027, 1, 2)));
        Assert.Equal("Christmas", PlanCalendar.PlanNameFor(ch, new DateOnly(2026, 12, 23))); // 7-day holiday beats 15-day range

        var month = PlanCalendar.Month(ch, 2026, 12);
        Assert.Equal(31, month.Count);
        Assert.Equal(6, month.Count(d => d.Plan.Name == "Christmas")); // 19 to 25, minus the pinned 20th
    }

    [Fact]
    public void Pinning_a_date_moves_it_between_rules_and_unpinning_removes_it()
    {
        ChannelConfig ch = ThreePlans();
        var day = new DateOnly(2026, 10, 12);
        PlanCalendar.PinDate(ch, day, "Weekend");
        Assert.Equal("Weekend", PlanCalendar.PlanNameFor(ch, day));
        PlanCalendar.PinDate(ch, day, "Christmas");
        Assert.Equal("Christmas", PlanCalendar.PlanNameFor(ch, day));
        Assert.Equal(2, ch.Calendar.Count(r => r.Kind == RuleKind.Dates)); // the original pin plus this one; the empty Weekend pin was dropped
        PlanCalendar.PinDate(ch, day, null);
        Assert.Equal("Normal Day", PlanCalendar.PlanNameFor(ch, day));
        Assert.Single(ch.Calendar, r => r.Kind == RuleKind.Dates);
    }

    [Fact]
    public void Renaming_and_removing_plans_keeps_the_rules_in_step()
    {
        ChannelConfig ch = ThreePlans();
        ch.RenamePlan(ch.FindPlan("Weekend")!, "Saturday and Sunday");
        Assert.Equal("Saturday and Sunday", ch.Calendar[0].Plan);
        Assert.Equal("Saturday and Sunday", PlanCalendar.PlanNameFor(ch, new DateOnly(2026, 10, 17)));

        DayPlan copy = ch.FindPlan("Christmas")!.Copy("Halloween");
        Assert.Equal("Halloween", copy.Name);
        Assert.Equal(["Doc"], copy.Slots[0].ShowList);
        Assert.NotSame(ch.FindPlan("Christmas")!.Slots[0], copy.Slots[0]);

        ch.RemovePlan(ch.FindPlan("Christmas")!);
        Assert.DoesNotContain(ch.Calendar, r => r.Plan == "Christmas");
        Assert.Equal("Normal Day", PlanCalendar.PlanNameFor(ch, new DateOnly(2026, 12, 25)));
    }

    private void Seed()
    {
        for (int i = 1; i <= 30; i++)
        {
            long id = _db.Insert(new MediaItem
            {
                ContentHash = $"s{i}", Path = $"/tv/short/e{i}.mkv", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch,
                Kind = MediaKind.Episode, KindSource = FactSource.Filename, DurationMs = 22 * Min, ProbedUtc = DateTimeOffset.UnixEpoch, Title = $"s{i}",
            });
            _db.UpsertEpisode(id, new EpisodeInfo { Show = "Short", Season = 1, Episode = i, Source = FactSource.Filename });
        }
        for (int i = 1; i <= 10; i++)
        {
            long id = _db.Insert(new MediaItem
            {
                ContentHash = $"d{i}", Path = $"/tv/doc/e{i}.mkv", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch,
                Kind = MediaKind.Episode, KindSource = FactSource.Filename, DurationMs = 44 * Min, ProbedUtc = DateTimeOffset.UnixEpoch, Title = $"d{i}",
            });
            _db.UpsertEpisode(id, new EpisodeInfo { Show = "Doc", Season = 1, Episode = i, Source = FactSource.Filename });
        }
        for (int i = 0; i < 12; i++)
            _db.Insert(new MediaItem { ContentHash = $"a{i}", Path = $"/ads/{i}.mp4", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch, Kind = MediaKind.Commercial, KindSource = FactSource.Filename, DurationMs = 30_000, ProbedUtc = DateTimeOffset.UnixEpoch });
    }

    [Fact]
    public void The_new_plan_takes_over_at_its_first_slot_and_the_night_before_runs_until_then()
    {
        Seed();
        var ch = new ChannelConfig
        {
            Number = "1",
            Defaults = new SlotDefaults { Pad = "30" },
            Gap = new GapConfig { HoldGrid = true, MaxMinutes = 12 },
            Plans =
            [
                // Short episodes (22 min, no chapters) pad to 25-minute blocks; Doc (44 min) to 45.
                new DayPlan { Name = "Normal Day", Slots = [new SlotConfig { Time = "06:00", Show = "Doc" }, new SlotConfig { Time = "20:00", Show = "Short" }] },
                new DayPlan { Name = "Weekend", Slots = [new SlotConfig { Time = "09:00", Show = "Doc" }] },
            ],
            Calendar = [new CalendarRule { Plan = "Weekend", Days = ["saturday", "sunday"] }],
        };

        // Friday 16 October 2026 at 22:00 through Saturday 10:00.
        var from = new DateTimeOffset(2026, 10, 16, 22, 0, 0, Offset);
        var log = new List<string>();
        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), ch, "0.0.5", log.Add).Build(from, from.AddHours(12));

        var saturdayNine = new DateTimeOffset(2026, 10, 17, 9, 0, 0, Offset);
        // Friday's 20:00 Short slot runs straight through the night, past Normal Day's 06:00 Doc slot, until Weekend's first slot at 09:00.
        var beforeNine = r.Programmes.Where(p => p.Start < saturdayNine).ToList();
        Assert.True(beforeNine.Count > 20, beforeNine.Count.ToString());
        Assert.All(beforeNine, p => Assert.Equal("Short", p.Title));
        Assert.Contains(r.Programmes, p => p.Start == saturdayNine && p.Title == "Doc");
        Assert.DoesNotContain(r.Programmes, p => p.Start < saturdayNine && p.Stop > saturdayNine);
        Assert.Contains(log, l => l.Contains("following the \"Weekend\" day plan"));
        Assert.Contains(log, l => l.Contains("following the \"Normal Day\" day plan"));
    }

    [Fact]
    public void A_channel_with_one_plan_builds_exactly_as_before()
    {
        Seed();
        var ch = new ChannelConfig
        {
            Number = "1",
            Defaults = new SlotDefaults { Pad = "30" },
            Gap = new GapConfig { HoldGrid = true, MaxMinutes = 12 },
            Schedule = [new SlotConfig { Time = "06:00", Show = "Doc" }, new SlotConfig { Time = "20:00", Show = "Short" }],
        };
        var from = new DateTimeOffset(2026, 10, 16, 5, 0, 0, Offset);
        var log = new List<string>();
        BuildResult r = new ChannelBuilder(_db, new EpisodePicker(_db, 1), ch, "0.0.5", log.Add).Build(from, from.AddHours(2));
        var six = from.AddHours(1);
        Assert.All(r.Programmes.Where(p => p.Start < six), p => Assert.Equal("Short", p.Title)); // yesterday's 20:00 slot until 06:00
        Assert.Contains(r.Programmes, p => p.Start == six && p.Title == "Doc");
        Assert.DoesNotContain(r.Programmes, p => p.Start < six && p.Stop > six);
        Assert.DoesNotContain(log, l => l.Contains("day plan"));
    }
}
