using TickleTV.Scheduler.Core.Config;

namespace TickleTV.Scheduler.Web.Services;

/// <summary>Small, saved display preferences such as how many results to show per page.</summary>
public static class UiPrefs
{
    public static readonly int[] PageSizes = [50, 100, 150, 200];

    public static int PageSize(ConfigStore store)
    {
        try { return Clamp(store.LoadLoose().Ui.PageSize); }
        catch { return 50; }
    }

    public static int SavePageSize(ConfigStore store, object? value)
    {
        int size = Clamp(int.TryParse(value?.ToString(), out int n) ? n : 50);
        try
        {
            SchedulerConfig cfg = store.LoadLoose();
            cfg.Ui.PageSize = size;
            store.Save(cfg);
        }
        catch { /* preference only */ }
        return size;
    }

    private static int Clamp(int n) => PageSizes.Contains(n) ? n : 50;
}
