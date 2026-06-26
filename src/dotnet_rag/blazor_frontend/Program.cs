using DotnetRag.Blazor.Components;
using DotnetRag.Blazor.Services;
using DotnetRag.Blazor.State;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

// Typed HttpClients
builder.Services.AddHttpClient<RagApiService>(client =>
    client.BaseAddress = new Uri(builder.Configuration["RagServer:BaseUrl"] ?? "http://localhost:8081"));
builder.Services.AddHttpClient<IngestorApiService>(client =>
    client.BaseAddress = new Uri(builder.Configuration["IngestorServer:BaseUrl"] ?? "http://localhost:8082"));
builder.Services.AddHttpClient<StreamingService>(client =>
    client.BaseAddress = new Uri(builder.Configuration["RagServer:BaseUrl"] ?? "http://localhost:8081"));

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
