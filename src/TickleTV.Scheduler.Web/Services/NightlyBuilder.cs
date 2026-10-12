using System.Text.Json;
using TickleTV.Scheduler.Core.Config;
using TickleTV.Scheduler.Core.Engine;

namespace TickleTV.Scheduler.Web.Services;

/// <summary>What the last automatic build did. Kept in auto-build.json in the data folder so it survives a restart.</summary>
public sealed class AutoBuildRecord
{
    public DateOnly? LastRunDay { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public bool Failed { get; set; }
    public string Summary { get; set; } = "";
    /// <summary>scheduled, or asked for (from the tray menu or the Build page).</summary>
    public string Reason { get; set; } = "";
}

/// <summary>
/// Builds the schedule once a night without anyone pressing Build. Checks the clock once a minute, so a computer
/// that was off at build time catches up as soon as the app is running again.
/// </summary>
public sealed class NightlyBuilder : BackgroundService
{
    private readonly ConfigStore _config;
    private readonly JobRunner _jobs;
    private readonly ILogger<NightlyBuilder> _log;
    private readonly object _gate = new();
    private AutoBuildRecord _record;

    public NightlyBuilder(ConfigStore config, JobRunner jobs, ILogger<NightlyBuilder> log)
    {
        _config = config;
        _jobs = jobs;
        _log = log;
        _record = Read();
    }

    public static string RecordPath => Path.Combine(ConfigLocations.DataFolder, "auto-build.json");
    public static string LogPath => Path.Combine(ConfigLocations.DataFolder, "auto-build.log");

    public AutoBuildRecord Record { get { lock (_gate) return _record; } }

    /// <summary>Pages subscribe to redraw when a record changes.</summary>
    public event Action? Changed;

    public DateTime? NextRun(AutoBuildConfig cfg) => AutoBuild.NextRun(cfg, DateTime.Now, Record.LastRunDay);

    /// <summary>Starts tonight's build right now. The tray menu and the Build page use this.</summary>
    public void RunNow() => Run("asked for");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { _log.LogWarning(ex, "The automatic build check failed."); }
            try { await timer.WaitForNextTickAsync(ct); } catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (_jobs.IsBusy) return;
        SchedulerConfig cfg;
        try { cfg = _config.LoadLoose(); } catch { return; }
        if (!AutoBuild.IsDue(cfg.AutoBuild, DateTime.Now, Record.LastRunDay)) return;
        Run("scheduled");
    }

    private void Run(string reason)
    {
        DateTimeOffset started = DateTimeOffset.Now;
        DateOnly today = DateOnly.FromDateTime(started.DateTime);
        SchedulerConfig cfg;
        try { cfg = _config.Load(); }
        catch (Exception ex)
        {
            Save(new AutoBuildRecord { LastRunDay = today, StartedAt = started, FinishedAt = DateTimeOffset.Now, Failed = true, Reason = reason, Summary = "Couldn't build: " + ex.Message });
            return;
        }

        JobState job;
        try { job = _jobs.StartBuild(cfg, new BuildOptions { Hours = AutoBuild.ClampDays(cfg.AutoBuild.Days) * 24 }); }
        catch (InvalidOperationException)
        {
            // A scan or a build is running; the next check tries again.
            return;
        }
        Save(new AutoBuildRecord { LastRunDay = today, StartedAt = started, Reason = reason, Summary = "Building now." });
        _ = Task.Run(async () =>
        {
            while (job.Running) await Task.Delay(500);
            Save(new AutoBuildRecord { LastRunDay = today, StartedAt = started, FinishedAt = job.FinishedAt, Failed = job.Failed, Reason = reason, Summary = job.Summary });
        });
    }

    private void Save(AutoBuildRecord record)
    {
        lock (_gate)
        {
            _record = record;
            try
            {
                Directory.CreateDirectory(ConfigLocations.DataFolder);
                File.WriteAllText(RecordPath, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
                if (record.FinishedAt is not null)
                    File.AppendAllText(LogPath, $"{record.FinishedAt:yyyy-MM-dd HH:mm} {(record.Failed ? "FAILED" : "ok")} ({record.Reason}) {record.Summary}{Environment.NewLine}");
            }
            catch (Exception ex) { _log.LogWarning(ex, "Couldn't save the automatic build record."); }
        }
        Changed?.Invoke();
    }

    private static AutoBuildRecord Read()
    {
        try
        {
            if (File.Exists(RecordPath))
                return JsonSerializer.Deserialize<AutoBuildRecord>(File.ReadAllText(RecordPath)) ?? new AutoBuildRecord();
        }
        catch { /* a bad record just means no history */ }
        return new AutoBuildRecord();
    }
}
