using System.Diagnostics;

namespace TickleTV.Scheduler.Web.Services;

/// <summary>Opens the app in the person's usual web browser.</summary>
public static class Browser
{
    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception)
        {
            // No browser is not fatal; the address is in the tray tooltip and the Start Menu shortcut.
        }
    }
}
