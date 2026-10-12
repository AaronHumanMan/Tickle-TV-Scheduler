using TickleTV.Scheduler.Core.Config;

namespace TickleTV.Scheduler.Web.Services;

/// <summary>When the app cannot start at all, say so somewhere a person will see it: a file in the data folder and, on Windows, a message box.</summary>
public static class StartupFailure
{
    public static string Path => System.IO.Path.Combine(ConfigLocations.DataFolder, "startup-error.txt");

    public static void Report(Exception ex, string url)
    {
        string text = $"Tickle TV couldn't start at {DateTimeOffset.Now:yyyy-MM-dd HH:mm}.\n\n{ex}\n";
        try
        {
            Directory.CreateDirectory(ConfigLocations.DataFolder);
            File.WriteAllText(Path, text);
        }
        catch { /* nowhere to write; the message box still shows */ }

        string why = ex.GetBaseException().Message;
        string hint = why.Contains("address", StringComparison.OrdinalIgnoreCase) || why.Contains("in use", StringComparison.OrdinalIgnoreCase)
            ? $"Another program is already using {url}. Close it, or if Tickle TV is already running, look for its icon by the clock."
            : $"Details were saved to {Path}.";
        if (OperatingSystem.IsWindows())
        {
            try { System.Windows.Forms.MessageBox.Show($"Tickle TV couldn't start.\n\n{why}\n\n{hint}", "Tickle TV Scheduler", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error); }
            catch { /* no desktop */ }
        }
        else
        {
            Console.Error.WriteLine(text + "\n" + hint);
        }
    }
}
