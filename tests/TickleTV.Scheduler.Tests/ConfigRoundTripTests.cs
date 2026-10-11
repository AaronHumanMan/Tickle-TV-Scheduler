using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Tests;

/// <summary>The app saves the config it edits; what it saves must load back the same.</summary>
public class ConfigRoundTripTests
{
    [Fact]
    public void Save_then_load_keeps_slots_and_settings()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tickletv-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "tickletv.yaml");
        try
        {
            var cfg = new SchedulerConfig
            {
                Catalog = "tickletv.sqlite",
                MediaRoots = [@"G:\Commercials", @"G:\Shows"],
                Next = new NextConfig { Lineup = @"C:\Next\config\lineup.json", SchemaVersion = "0.0.5" },
                Channels =
                [
                    new ChannelConfig
                    {
                        Number = "1", Name = "Tickle TV", TvgId = "tickle.1", HorizonHours = 24,
                        Defaults = new SlotDefaults { Pad = "30", MaxBreaks = 3, NoChapterPad = "5" },
                        Ads = new AdsConfig { Pool = "kind = commercial", CooldownMinutes = 120 },
                        Bumpers = new BumpersConfig { Placement = "show_edges" },
                        Gap = new GapConfig { HoldGrid = true, MaxMinutes = 12 },
                        Schedule =
                        [
                            new SlotConfig { Time = "06:00", Show = "How It's Made", Order = "shuffle" },
                            new SlotConfig { Time = "20:00", Show = "TNG", Order = "chronological", Pad = "60", MaxBreaks = 4 },
                        ],
                    },
                ],
            };
            cfg.Save(path);

            SchedulerConfig back = SchedulerConfig.Load(path);
            Assert.Equal(cfg.MediaRoots, back.MediaRoots);
            Assert.Equal(@"C:\Next\config\lineup.json", back.Next.Lineup);
            ChannelConfig ch = back.Channels.Single();
            Assert.Equal("Tickle TV", ch.Name);
            Assert.Equal(120, ch.Ads.CooldownMinutes);
            Assert.Equal(BumperPlacement.ShowEdges, ch.Bumpers.PlacementMode);
            Assert.True(ch.Gap.HoldGrid);
            Assert.Equal(2, ch.Schedule.Count);
            Assert.Equal(new TimeOnly(20, 0), ch.Schedule[1].StartTime);
            Assert.Equal(PaddingMode.Hour, ch.Schedule[1].PaddingPolicy(ch.Defaults).Mode);
            Assert.Equal(4, ch.Schedule[1].BreakPolicy(ch.Defaults).MaxMidBreaks);
            Assert.Equal(OrderMode.Shuffle, ch.Schedule[0].OrderMode);
            Assert.Null(ch.Schedule[0].Pad);

            // Computed properties must not have been written into the file.
            string yaml = File.ReadAllText(path);
            Assert.DoesNotContain("padding_mode", yaml);
            Assert.DoesNotContain("start_time", yaml);
            Assert.DoesNotContain("base_directory", yaml);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
