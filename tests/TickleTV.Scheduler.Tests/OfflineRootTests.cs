using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Media;

namespace TickleTV.Scheduler.Tests;

/// <summary>A media root that cannot be opened (a share that is off) must not empty the catalog: its files stay as they were.</summary>
public class OfflineRootTests : IDisposable
{
    private readonly string _root;
    private readonly CatalogDb _db = CatalogDb.InMemory();

    private sealed class StubProber : IMediaProber
    {
        public Task<MediaProbe> ProbeAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(new MediaProbe { Path = path, DurationMs = 30_000, Chapters = [], Streams = [new MediaStreamInfo(0, "video", "h264")] });
    }

    public OfflineRootTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tickletv-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string MakeFile(string relative, string content)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public async Task Files_under_an_unreachable_root_are_not_marked_missing()
    {
        string local = Path.Combine(_root, "Local");
        string share = Path.Combine(_root, "Share");
        MakeFile(@"Local\Commercials\Pepsi Commercial 1991.mp4", "pepsi");
        MakeFile(@"Local\Commercials\Coke Commercial 1992.mp4", "coke");
        MakeFile(@"Share\Commercials\Ford Commercial 1993.mp4", "ford");
        MakeFile(@"Share\Commercials\Chevy Commercial 1994.mp4", "chevy");
        var scanner = new Scanner(_db, new StubProber());

        ScanReport first = await scanner.ScanAsync([local, share]);
        Assert.Equal(4, first.Added);
        Assert.Empty(first.UnreachableRoots);

        // The share goes away, and one local file really is deleted.
        Directory.Move(share, share + ".offline");
        File.Delete(Path.Combine(local, "Commercials", "Coke Commercial 1992.mp4"));

        ScanReport second = await scanner.ScanAsync([local, share]);

        Assert.Equal([Path.GetFullPath(share)], second.UnreachableRoots);
        Assert.Equal(1, second.Missing);
        Assert.Contains(second.Errors, e => e.Contains("Couldn't open the folder", StringComparison.Ordinal) && e.Contains("left as they were", StringComparison.Ordinal));
        var present = _db.ListItems(includeMissing: false).Select(i => Path.GetFileName(i.Path)).Order().ToList();
        Assert.Equal(["Chevy Commercial 1994.mp4", "Ford Commercial 1993.mp4", "Pepsi Commercial 1991.mp4"], present);

        // The share comes back: nothing to re-probe, nothing missing.
        Directory.Move(share + ".offline", share);
        ScanReport third = await scanner.ScanAsync([local, share]);
        Assert.Empty(third.UnreachableRoots);
        Assert.Equal(3, third.Unchanged);
        Assert.Equal(0, third.Added);
    }
}
