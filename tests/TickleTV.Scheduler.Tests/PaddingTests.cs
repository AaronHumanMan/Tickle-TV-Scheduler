using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Tests;

public class PaddingTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);
    private static DateTimeOffset At(int h, int m, int s = 0) => new(2026, 10, 10, h, m, s, Offset);

    [Fact]
    public void HalfHour_22m10s_episode_at_8pm_ends_at_830()
    {
        var policy = new PaddingPolicy { Mode = PaddingMode.HalfHour, MinPadMs = 2 * 60_000 };
        long content = 22 * 60_000 + 10_000;

        Assert.Equal(At(20, 30), Padding.SlotEnd(At(20, 0), content, policy));
        Assert.Equal(7 * 60_000 + 50_000, Padding.PadMs(At(20, 0), content, policy));
    }

    [Fact]
    public void MinPad_pushes_to_next_boundary_when_remainder_is_too_small()
    {
        var policy = new PaddingPolicy { Mode = PaddingMode.HalfHour, MinPadMs = 2 * 60_000 };
        long content = 29 * 60_000; // leaves 1:00 without min pad

        Assert.Equal(At(21, 0), Padding.SlotEnd(At(20, 0), content, policy));
    }

    [Fact]
    public void Next10_is_measured_on_the_wall_clock_not_from_start()
    {
        var policy = new PaddingPolicy { Mode = PaddingMode.Next10 };
        long content = 4 * 60_000;

        // 8:03 + 4:00 = 8:07, snaps to 8:10 (not 8:13).
        Assert.Equal(At(8, 10), Padding.SlotEnd(At(8, 3), content, policy));
    }

    [Fact]
    public void Exact_boundary_needs_no_pad()
    {
        var policy = new PaddingPolicy { Mode = PaddingMode.Hour };
        Assert.Equal(0, Padding.PadMs(At(9, 0), 60 * 60_000, policy));
    }

    [Fact]
    public void None_mode_ends_when_content_ends()
    {
        var policy = new PaddingPolicy { Mode = PaddingMode.None };
        Assert.Equal(At(9, 22, 10), Padding.SlotEnd(At(9, 0), 22 * 60_000 + 10_000, policy));
    }

    [Fact]
    public void Next5_crossing_midnight_still_snaps()
    {
        var policy = new PaddingPolicy { Mode = PaddingMode.Next5 };
        DateTimeOffset start = new(2026, 10, 10, 23, 58, 0, Offset);
        DateTimeOffset end = Padding.SlotEnd(start, 4 * 60_000, policy);
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 0, 5, 0, Offset), end);
    }
}
