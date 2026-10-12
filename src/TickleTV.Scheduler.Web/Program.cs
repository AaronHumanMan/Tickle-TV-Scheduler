using TickleTV.Scheduler.Web.Components;
using TickleTV.Scheduler.Web.Services;

// --quiet starts the app without opening the browser; the sign-in shortcut uses it.
bool quiet = args.Contains("--quiet");
const string DefaultUrl = "http://localhost:5062";

// Only one copy runs at a time. A second launch just opens the browser to the copy already running.
using var single = new Mutex(initiallyOwned: true, @"Local\TickleTV.Scheduler.Web", out bool firstCopy);
if (!firstCopy)
{
    if (!quiet) Browser.Open(DefaultUrl);
    return;
}

// The installed program carries its own wwwroot. Use the program's folder as the content root so the pages find
// their stylesheet and scripts however the app was started (a shortcut, the sign-in entry, or a different folder).
// During development there is no wwwroot beside the binary, so the project folder stays the content root.
var options = new WebApplicationOptions { Args = args };
if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")))
    options = new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory };
var builder = WebApplication.CreateBuilder(options);
if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls(DefaultUrl);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<CatalogAccess>();
builder.Services.AddSingleton<FileOpener>();
builder.Services.AddSingleton<LibraryActions>();
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddSingleton<NightlyBuilder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NightlyBuilder>());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
CsvExport.Map(app);
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

try { await app.StartAsync(); }
catch (Exception ex)
{
    StartupFailure.Report(ex, DefaultUrl);
    return;
}

string url = app.Urls.FirstOrDefault(u => !u.Contains('*') && !u.Contains('+') && !u.Contains("0.0.0.0")) ?? DefaultUrl;
if (!quiet) Browser.Open(url);
if (OperatingSystem.IsWindows())
{
    var nightly = app.Services.GetRequiredService<NightlyBuilder>();
    TrayIcon.Start(url, nightly.RunNow, app.Lifetime.StopApplication);
    app.Lifetime.ApplicationStopping.Register(TrayIcon.Stop);
}
await app.WaitForShutdownAsync();
