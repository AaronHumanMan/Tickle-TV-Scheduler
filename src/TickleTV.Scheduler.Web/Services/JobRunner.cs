using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;
using TickleTV.Scheduler.Core.Media;

namespace TickleTV.Scheduler.Web.Services;

/// <summary>What the app is doing in the background right now: one scan or build at a time, with progress.</summary>
public sealed class JobState
{
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public bool Running { get; set; }
    public bool Failed { get; set; }
    public int Done { get; set; }
    public int Total { get; set; }
    public string Current { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Log { get; } = [];
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    public object? Result { get; set; }
    public double Percent => Total <= 0 ? 0 : Math.Min(100, 100.0 * Done / Total);
}

/// <summary>Runs scans and builds on a background thread so pages stay responsive, and tells them when something changes.</summary>
public sealed class JobRunner
{
    private readonly object _gate = new();
    private readonly CatalogAccess _catalog;

    public JobRunner(CatalogAccess catalog) => _catalog = catalog;

    public JobState? Current { get; private set; }
    public JobState? Last { get; private set; }
    public bool IsBusy => Current?.Running == true;

    /// <summary>Pages subscribe to redraw when progress changes.</summary>
    public event Action? Changed;

    private void Notify() => Changed?.Invoke();

    private DateTimeOffset _lastLogNotify = DateTimeOffset.MinValue;

    private void AddLog(JobState job, string line)
    {
        bool tell;
        lock (job.Log)
        {
            job.Log.Add(line);
            if (job.Log.Count > 400) job.Log.RemoveRange(0, job.Log.Count - 400);
            // A scan logs a line per file, tens of thousands in a big library. Pages re-read the catalog on every
            // change notice, so pass these on a few times a second at most; Finish sends the last one regardless.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            tell = now - _lastLogNotify >= TimeSpan.FromMilliseconds(400);
            if (tell) _lastLogNotify = now;
        }
        if (tell) Notify();
    }

    public JobState StartScan(IReadOnlyList<string> roots)
    {
        lock (_gate)
        {
            if (IsBusy) throw new InvalidOperationException("Something is already running. Wait for it to finish.");
            var job = new JobState { Kind = "scan", Title = roots.Count == 1 ? $"Scanning {roots[0]}" : $"Scanning {roots.Count} folders", Running = true };
            Current = job;
            Notify();

            _ = Task.Run(async () =>
            {
                try
                {
                    var progress = new Progress<ScanProgress>(p =>
                    {
                        job.Done = p.Done;
                        job.Total = p.Total;
                        job.Current = Path.GetFileName(p.CurrentFile);
                        if (p.Done % 25 == 0 || p.Done == p.Total) Notify();
                    });
                    // The scan gets its own connection and stays out of the pages' gate: the catalog is in WAL mode, so
                    // pages keep reading while it writes, and the app stays usable for the hours a big library takes.
                    ScanReport report;
                    using (var db = new CatalogDb(_catalog.CatalogPath))
                        report = await new Scanner(db, new Ffprobe(), line => AddLog(job, line)).ScanAsync(roots, CancellationToken.None, progress);
                    job.Result = report;
                    bool anySidecar = report.WithSidecar > 0 || _catalog.Run(db => db.CountWithSidecar()) > 0;
                    job.Summary = $"Added {report.Added}, updated {report.Updated}, moved {report.Moved}, unchanged {report.Unchanged}, missing {report.Missing}."
                                  + (report.WithSidecar > 0 ? $" {report.WithSidecar} took their details from .nfo files." : anySidecar ? "" : " No .nfo details files were found next to your videos (see Help: How files get sorted).")
                                  + (report.Unclassified.Count > 0 ? $" {report.Unclassified.Count} need attention." : "")
                                  + (report.UnreachableRoots.Count > 0 ? $" Couldn't open {Folders(report.UnreachableRoots)}: if that is a network share or an external drive, check it is on and connected, then scan again. Its files were left as they were." : "")
                                  + (report.Errors.Count > report.UnreachableRoots.Count ? $" {report.Errors.Count - report.UnreachableRoots.Count} problems." : "");
                }
                catch (Exception ex)
                {
                    job.Failed = true;
                    job.Summary = "The scan stopped with a problem: " + ex.Message;
                }
                finally
                {
                    Finish(job);
                }
            });
            return job;
        }
    }

    public JobState StartBuild(SchedulerConfig cfg, BuildOptions options)
    {
        lock (_gate)
        {
            if (IsBusy) throw new InvalidOperationException("Something is already running. Wait for it to finish.");
            var job = new JobState { Kind = options.DryRun ? "preview" : "build", Title = options.DryRun ? "Previewing the schedule" : "Building the schedule", Running = true };
            Current = job;
            Notify();

            _ = Task.Run(() =>
            {
                try
                {
                    var reports = BuildRunner.Run(cfg, options, line => AddLog(job, line));
                    job.Result = reports;
                    int items = reports.Sum(r => r.ItemCount);
                    int warnings = reports.Sum(r => r.Warnings.Count);
                    var errors = reports.Where(r => r.Error is not null).Select(r => r.Error!).ToList();
                    job.Failed = errors.Count > 0;
                    job.Summary = errors.Count > 0
                        ? string.Join(" ", errors)
                        : options.DryRun
                            ? $"Preview ready: {reports.Sum(r => r.Programmes.Count)} programmes, {items} items{(warnings > 0 ? $", {warnings} things to look at" : "")}. Nothing was written."
                            : $"Done. {reports.Sum(r => r.Programmes.Count)} programmes written for ErsatzTV{(warnings > 0 ? $", {warnings} things to look at" : "")}.";
                }
                catch (Exception ex)
                {
                    job.Failed = true;
                    job.Summary = "The build stopped with a problem: " + ex.Message;
                }
                finally
                {
                    Finish(job);
                }
            });
            return job;
        }
    }

    private static string Folders(IReadOnlyList<string> roots) => roots.Count == 1 ? roots[0] : $"{roots.Count} folders ({string.Join("; ", roots)})";

    private void Finish(JobState job)
    {
        job.Running = false;
        job.FinishedAt = DateTimeOffset.Now;
        lock (_gate)
        {
            Last = job;
            if (ReferenceEquals(Current, job)) Current = null;
        }
        Notify();
    }
}
