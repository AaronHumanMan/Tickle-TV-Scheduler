using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Tests;

/// <summary>The nightly automatic build: when it is due, when the next one is, and that its settings survive a save.</summary>
public class AutoBuildTests
{
    private static readonly AutoBuildConfig At3 = new() { Enabled = true, Time = "03:00", Days = 2 };
    private static DateTime On(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0);

    [Theory]
    [InlineData("03:00", 3, 0)]
    [InlineData("4:30", 4, 30)]
    [InlineData("11:45 PM", 23, 45)]
    [InlineData("7 pm", 19, 0)]
    [InlineData("not a time", 3, 0)]
    [InlineData(null, 3, 0)]
    public void Build_time_is_read_leniently_and_falls_back_to_3am(string? text, int hour, int minute)
    {
        Assert.Equal(new TimeOnly(hour, minute), AutoBuild.ParseTime(text));
    }

    [Fact]
    public void Not_due_before_the_build_time()
    {
        Assert.False(AutoBuild.IsDue(At3, On(11, 2, 59), null));
    }

    [Fact]
    public void Due_at_the_build_time_when_it_has_never_run()
    {
        Assert.True(AutoBuild.IsDue(At3, On(11, 3, 0), null));
    }

    [Fact]
    public void Not_due_again_on_a_day_it_already_ran()
    {
        Assert.False(AutoBuild.IsDue(At3, On(11, 15, 0), new DateOnly(2026, 10, 11)));
    }

    [Fact]
    public void Catches_up_later_in_the_day_when_the_computer_was_off_at_3am()
    {
        Assert.True(AutoBuild.IsDue(At3, On(11, 9, 0), new DateOnly(2026, 10, 10)));
    }

    [Fact]
    public void Never_due_when_switched_off()
    {
        var off = new AutoBuildConfig { Enabled = false, Time = "03:00" };
        Assert.False(AutoBuild.IsDue(off, On(11, 9, 0), null));
        Assert.Null(AutoBuild.NextRun(off, On(11, 9, 0), null));
    }

    [Fact]
    public void Next_run_is_tonight_tomorrow_or_right_now()
    {
        Assert.Equal(On(11, 3), AutoBuild.NextRun(At3, On(11, 1), new DateOnly(2026, 10, 10)));
        Assert.Equal(On(12, 3), AutoBuild.NextRun(At3, On(11, 9), new DateOnly(2026, 10, 11)));
        Assert.Equal(On(11, 9), AutoBuild.NextRun(At3, On(11, 9), new DateOnly(2026, 10, 10)));
    }

    [Fact]
    public void Days_are_kept_within_what_a_build_allows()
    {
        Assert.Equal(1, AutoBuild.ClampDays(0));
        Assert.Equal(BuildOptions.MaxDays, AutoBuild.ClampDays(999));
    }

    [Fact]
    public void Settings_survive_a_save_and_default_when_absent()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tickletv-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "tickletv.yaml");
        try
        {
            var cfg = new SchedulerConfig
            {
                Next = new NextConfig { Lineup = @"C:\Next\config\lineup.json" },
                AutoBuild = new AutoBuildConfig { Enabled = false, Time = "04:30", Days = 3 },
                Channels = [new ChannelConfig { Number = "1", Name = "Test", Schedule = [new SlotConfig { Time = "06:00", Show = "Show" }] }],
            };
            cfg.Save(path);
            string yaml = File.ReadAllText(path);
            Assert.Contains("auto_build:", yaml);

            SchedulerConfig back = SchedulerConfig.Load(path);
            Assert.False(back.AutoBuild.Enabled);
            Assert.Equal(new TimeOnly(4, 30), back.AutoBuild.TimeOfDay);
            Assert.Equal(3, back.AutoBuild.Days);

            var fresh = new SchedulerConfig();
            Assert.True(fresh.AutoBuild.Enabled);
            Assert.Equal(new TimeOnly(3, 0), fresh.AutoBuild.TimeOfDay);
            Assert.Equal(2, fresh.AutoBuild.Days);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
