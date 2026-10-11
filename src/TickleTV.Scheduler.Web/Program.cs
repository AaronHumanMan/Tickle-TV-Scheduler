using TickleTV.Scheduler.Web.Components;
using TickleTV.Scheduler.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<CatalogAccess>();
builder.Services.AddSingleton<FileOpener>();
builder.Services.AddSingleton<LibraryActions>();
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<JobRunner>();

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

app.Run();
