using TickleTV.Scheduler.Core.Catalog;
using TickleTV.Scheduler.Core.Config;

namespace TickleTV.Scheduler.Web.Services;

/// <summary>
/// Opens the catalog for one unit of work at a time. The catalog object is not thread-safe, so every page
/// call goes through <see cref="Run"/>, which opens a fresh connection and serialises writers.
/// </summary>
public sealed class CatalogAccess
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string CatalogPath => ConfigLocations.DefaultCatalogPath();
    public string? ConfigPath => ConfigLocations.Find();
    public bool CatalogExists => File.Exists(CatalogPath);

    public T Run<T>(Func<CatalogDb, T> work)
    {
        _gate.Wait();
        try
        {
            using var db = new CatalogDb(CatalogPath);
            return work(db);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Run(Action<CatalogDb> work) => Run(db => { work(db); return 0; });
}
