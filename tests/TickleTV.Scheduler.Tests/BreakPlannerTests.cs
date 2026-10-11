using TickleTV.Scheduler.Core.Engine;
using TickleTV.Scheduler.Core.Media;

namespace TickleTV.Scheduler.Tests;

public class BreakPlannerTests
{
    private const long Min = 60_000;

    private static List<Chapter> Chapters(params long[] startsMs)
    {
        var list = new List<Chapter>();
        for (int i = 0; i < startsMs.Length; i++)
        {
            long end = i + 1 < startsMs.Length ? startsMs[i + 1] : startsMs[i] + Min;
            list.Add(new Chapter(startsMs[i], end, $"Chapter {i + 1}"));
        }
        return list;
    }

    [Fact]
    public void Four_chapters_become_four_acts_with_three_breaks()
    {
        long duration = 22 * Min;
        var chapters = Chapters(0, 5 * Min, 11 * Min, 17 * Min);
        var acts = BreakPlanner.PlanActs(duration, chapters, new BreakPolicy { MaxMidBreaks = 3, MinActMs = 2 * Min });

        Assert.Equal(4, acts.Count);
        Assert.Equal(new Act(0, 5 * Min), acts[0]);
        Assert.Equal(new Act(5 * Min, 11 * Min), acts[1]);
        Assert.Equal(new Act(11 * Min, 17 * Min), acts[2]);
        Assert.Equal(new Act(17 * Min, 22 * Min), acts[3]);
        Assert.Equal(duration, acts.Sum(a => a.DurationMs));
    }

    [Fact]
    public void Acts_cover_the_whole_file_with_no_gaps()
    {
        long duration = 44 * Min + 123;
        var chapters = Chapters(0, 3 * Min, 9 * Min, 20 * Min, 31 * Min, 40 * Min);
        var acts = BreakPlanner.PlanActs(duration, chapters, new BreakPolicy { MaxMidBreaks = 3, MinActMs = 2 * Min });

        Assert.Equal(0, acts[0].InMs);
        Assert.Equal(duration, acts[^1].OutMs);
        for (int i = 1; i < acts.Count; i++)
            Assert.Equal(acts[i - 1].OutMs, acts[i].InMs);
    }

    [Fact]
    public void Too_many_chapters_are_thinned_to_max_breaks_near_even_spacing()
    {
        long duration = 60 * Min;
        var chapters = Chapters(0, 5 * Min, 12 * Min, 20 * Min, 29 * Min, 38 * Min, 46 * Min, 55 * Min);
        var acts = BreakPlanner.PlanActs(duration, chapters, new BreakPolicy { MaxMidBreaks = 3, MinActMs = 2 * Min, ColdOpenBreak = false });

        Assert.Equal(4, acts.Count);
        // Targets at 15, 30, 45 min: nearest candidates are 12, 29, 46.
        Assert.Equal(12 * Min, acts[0].OutMs);
        Assert.Equal(29 * Min, acts[1].OutMs);
        Assert.Equal(46 * Min, acts[2].OutMs);
    }

    [Fact]
    public void Chapters_too_close_to_the_ends_are_ignored()
    {
        long duration = 22 * Min;
        var chapters = Chapters(0, 30_000, 11 * Min, 21 * Min + 30_000);
        var acts = BreakPlanner.PlanActs(duration, chapters, new BreakPolicy { MaxMidBreaks = 3, MinActMs = 2 * Min });

        Assert.Equal(2, acts.Count);
        Assert.Equal(11 * Min, acts[0].OutMs);
    }

    [Fact]
    public void Without_chapters_fallback_splits_evenly()
    {
        long duration = 30 * Min;
        var acts = BreakPlanner.PlanActs(duration, [], new BreakPolicy { FallbackBreaks = 2, MinActMs = Min });

        Assert.Equal(3, acts.Count);
        Assert.Equal(10 * Min, acts[0].OutMs);
        Assert.Equal(20 * Min, acts[1].OutMs);
    }

    [Fact]
    public void Without_chapters_and_no_fallback_is_a_single_act()
    {
        var acts = BreakPlanner.PlanActs(30 * Min, [], new BreakPolicy());
        Assert.Single(acts);
        Assert.Equal(new Act(0, 30 * Min), acts[0]);
    }

    [Fact]
    public void Chapters_that_give_no_usable_cut_fall_back_to_even_spacing()
    {
        long duration = 22 * Min;
        var chapters = Chapters(0, 21 * Min + 30_000); // one mark 30 s before the end
        var acts = BreakPlanner.PlanActs(duration, chapters, new BreakPolicy { MaxMidBreaks = 3, MinActMs = 2 * Min, FallbackBreaks = 2 });

        Assert.Equal(3, acts.Count);
    }
}
