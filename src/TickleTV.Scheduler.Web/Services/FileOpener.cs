using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TickleTV.Scheduler.Web.Services;

/// <summary>
/// Opens media on the computer running the app: in the default video player, or in the file manager with
/// the file selected. The app is meant to run on the same machine as the media, so this is a local action.
/// </summary>
public sealed class FileOpener
{
    public string? Open(string path)
    {
        if (!File.Exists(path)) return "That file is not there any more. Run a scan to update the catalog.";
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not open the file: {ex.Message}";
        }
    }

    public string? ShowInFolder(string path)
    {
        if (!File.Exists(path)) return "That file is not there any more. Run a scan to update the catalog.";
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start(new ProcessStartInfo("open", $"-R \"{path}\"") { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = false });
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not show the folder: {ex.Message}";
        }
    }
}
