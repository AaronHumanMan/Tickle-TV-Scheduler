using System.Diagnostics;
using Microsoft.Data.Sqlite;
using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Media;

namespace TickleTV.Scheduler.Tests;

/// <summary>
/// A scan can take hours on a big library. The app must stay usable meanwhile, so another connection to the same
/// catalog file has to be able to read (and write) while the scan is in progress.
/// </summary>
public class ScanConcurrencyTests
{
    /// <summary>A prober that takes a while per file, like ffprobe does, without needing ffprobe.</summary>
    private sealed class SlowProber : IMediaProber
    {
        public async Task<MediaProbe> ProbeAsync(string path, CancellationToken ct = default)
        {
            await Task.Delay(150, ct);
            return new MediaProbe { Path = path, DurationMs = 30_000, Chapters = [], Streams = [] };
        }
    }

    [Fact]
    public async Task Another_connection_can_read_and_write_the_catalog_while_a_scan_runs()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tickletv-scan-" + Guid.NewGuid().ToString("N"));
        string media = Path.Combine(dir, "Commercials");
        Directory.CreateDirectory(media);
        for (int i = 0; i < 12; i++) File.WriteAllBytes(Path.Combine(media, $"Brand {i} Commercial.mp4"), Enumerable.Repeat((byte)i, 64).ToArray()); // distinct bytes, or the scanner would treat them as one moved file
        string catalog = Path.Combine(dir, "tickletv.sqlite");
        CatalogDb? scanDb = null;
        try
        {
            scanDb = new CatalogDb(catalog);
            Task<ScanReport> scan = new Scanner(scanDb, new SlowProber(), null).ScanAsync([media]);

            await Task.Delay(400); // the scan is a few files in and still has over a second to go
            Assert.False(scan.IsCompleted);

            var clock = Stopwatch.StartNew();
            using (var pageDb = new CatalogDb(catalog))
            {
                int seen = pageDb.CountItems();
                pageDb.AddCategory("Toys");
                Assert.True(seen >= 1, "the page should see files the scan has already written");
                Assert.Contains(pageDb.ListCategories(), c => c.Slug == "toys");
            }
            Assert.True(clock.ElapsedMilliseconds < 1000, $"a page read took {clock.ElapsedMilliseconds} ms while the scan ran");

            ScanReport report = await scan;
            Assert.Equal(12, report.Added);
        }
        finally
        {
            scanDb?.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* a leftover temp folder is not a failure */ }
        }
    }
}
