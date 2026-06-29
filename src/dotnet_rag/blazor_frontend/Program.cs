using DotnetRag.Blazor.Components;
using DotnetRag.Blazor.Services;
using DotnetRag.Blazor.State;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

var ragServerBaseUrl = GetBaseUrl(
    builder.Configuration,
    "RagServer:BaseUrl",
    "RagApi:BaseUrl",
    "http://localhost:8081");
var ingestorServerBaseUrl = GetBaseUrl(
    builder.Configuration,
    "IngestorServer:BaseUrl",
    "IngestorApi:BaseUrl",
    "http://localhost:8082");

// Typed HttpClients
builder.Services.AddHttpClient<RagApiService>(client =>
    client.BaseAddress = new Uri(ragServerBaseUrl));
builder.Services.AddHttpClient<IngestorApiService>(client =>
    client.BaseAddress = new Uri(ingestorServerBaseUrl));
builder.Services.AddHttpClient<StreamingService>(client =>
{
    client.BaseAddress = new Uri(ragServerBaseUrl);
    client.Timeout = Timeout.InfiniteTimeSpan;
});

// Scoped state — one instance per SignalR circuit
builder.Services.AddScoped<ChatState>();
builder.Services.AddScoped<CollectionsState>();
builder.Services.AddScoped<SettingsState>();
builder.Services.AddScoped<NotificationState>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();

static string GetBaseUrl(
    IConfiguration configuration,
    string primaryKey,
    string aliasKey,
    string fallback)
{
    var value = configuration[primaryKey];
    if (!string.IsNullOrWhiteSpace(value))
    {
        return value;
    }

    value = configuration[aliasKey];
    return string.IsNullOrWhiteSpace(value) ? fallback : value;
}
