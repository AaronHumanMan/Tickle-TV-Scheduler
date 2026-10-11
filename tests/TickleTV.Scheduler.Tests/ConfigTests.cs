using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Tests;

public class ConfigTests
{
    private const string Yaml = """
        catalog: db/cat.sqlite
        next:
          lineup: C:\Next\config\lineup.json
          schema_version: 0.0.4
        channels:
          - number: "1"
            name: Tickle TV
            horizon_hours: 48
            defaults:
              pad: "30"
              max_breaks: 3
            ads:
              pool: kind = commercial AND era within 3
              cooldown_minutes: 120
            schedule:
              - time: "20:00"
                show: TNG
                order: chronological
                pad: "60"
                max_breaks: 4
              - time: "22:00"
                show: The Simpsons
                order: shuffle
        """;

    [Fact]
    public void Parses_channels_slots_and_overrides()
    {
        SchedulerConfig cfg = SchedulerConfig.Parse(Yaml, @"C:\cfg");

        Assert.Equal(@"C:\cfg\db\cat.sqlite", cfg.ResolvePath(cfg.Catalog));
        Assert.Equal(@"C:\Next\config\lineup.json", cfg.ResolvePath(cfg.Next.Lineup));
        Assert.Equal("0.0.4", cfg.Next.SchemaVersion);

        ChannelConfig ch = cfg.Channels.Single();
        Assert.Equal("tickle.1", ch.TvgId); // defaulted from the number
        Assert.Equal(48, ch.HorizonHours);
        Assert.Equal(120, ch.Ads.CooldownMinutes);

        SlotConfig tng = ch.Schedule[0];
        Assert.Equal(new TimeOnly(20, 0), tng.StartTime);
        Assert.Equal(OrderMode.Chronological, tng.OrderMode);
        Assert.Equal(PaddingMode.Hour, tng.PaddingPolicy(ch.Defaults).Mode);
        Assert.Equal(4, tng.BreakPolicy(ch.Defaults).MaxMidBreaks);

        SlotConfig simpsons = ch.Schedule[1];
        Assert.Equal(OrderMode.Shuffle, simpsons.OrderMode);
        Assert.Equal(PaddingMode.HalfHour, simpsons.PaddingPolicy(ch.Defaults).Mode); // inherited
        Assert.Equal(3, simpsons.BreakPolicy(ch.Defaults).MaxMidBreaks);
    }

    [Fact]
    public void Rejects_bad_order_and_missing_schedule()
    {
        Assert.Throws<InvalidDataException>(() => SchedulerConfig.Parse("channels:\n  - number: \"1\"\n    schedule: []\n", @"C:\x"));
        Assert.Throws<InvalidDataException>(() => SchedulerConfig.Parse("channels:\n  - number: \"1\"\n    schedule:\n      - time: \"20:00\"\n        show: X\n        order: sideways\n", @"C:\x"));
    }
}
