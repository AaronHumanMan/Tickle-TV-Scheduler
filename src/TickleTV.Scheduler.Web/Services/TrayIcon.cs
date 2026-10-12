using System.Runtime.Versioning;
using WF = System.Windows.Forms;

namespace TickleTV.Scheduler.Web.Services;

/// <summary>
/// The little icon by the clock while Tickle TV runs: double-click opens the app, the menu builds tonight's
/// schedule now or quits. It runs its own message loop on a Windows Forms thread beside the web server.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayIcon
{
    public static void Start(string url, Action buildNow, Action quit)
    {
        var thread = new Thread(() => Run(url, buildNow, quit)) { IsBackground = true, Name = "tray" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Takes the icon down when the app is stopping for any other reason.</summary>
    public static void Stop()
    {
        try { WF.Application.Exit(); } catch { /* the loop may already be gone */ }
    }

    private static void Run(string url, Action buildNow, Action quit)
    {
        WF.Application.EnableVisualStyles();
        string iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        using System.Drawing.Icon icon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Application;
        using var menu = new WF.ContextMenuStrip();
        var open = menu.Items.Add("Open Tickle TV", null, (_, _) => Browser.Open(url));
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add("Build tonight's schedule now", null, (_, _) => buildNow());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Quit Tickle TV", null, (_, _) => { WF.Application.ExitThread(); quit(); });

        using var tray = new WF.NotifyIcon
        {
            Icon = icon,
            Text = "Tickle TV Scheduler (" + url + ")",
            Visible = true,
            ContextMenuStrip = menu,
        };
        tray.DoubleClick += (_, _) => Browser.Open(url);
        WF.Application.Run();
        tray.Visible = false;
    }
}
