using System.Text.Json;
using DotnetRag.Reranker.Services;
using DotnetRag.Reranker.Providers;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Models;
using Microsoft.OpenApi.Models;

DotnetRagEnvironmentBootstrap.LoadSharedLocalEnvironment();

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
});

var config = RerankerServiceConfiguration.FromEnvironment();
builder.Services.AddSingleton(config);

builder.Logging.SetMinimumLevel(config.LogLevel.ToUpperInvariant() switch
{
    "DEBUG" or "NOTSET" => LogLevel.Debug,
    "WARNING" or "WARN" => LogLevel.Warning,
    "ERROR" => LogLevel.Error,
    "CRITICAL" => LogLevel.Critical,
    _ => LogLevel.Information
});

builder.Services.AddHttpClient("reranker-openai", client =>
{
    client.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
});

builder.Services.AddHttpClient("reranker-ollama", client =>
{
    client.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
});

builder.Services.AddSingleton<IRerankerProvider, OpenAiCompatibleRerankerProvider>();
builder.Services.AddSingleton<IRerankerProvider, OllamaRerankerProvider>();
builder.Services.AddSingleton<IRerankerProvider, LexicalRerankerProvider>();
builder.Services.AddSingleton<RerankerOrchestrator>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "APIs for NVIDIA RAG Reranker Service (v1)",
        Version = "1.0.0",
        Description = "Internal reranking API used by dotnet-rag-server."
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "swagger";
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "NVIDIA Reranker API v1");
});

app.MapGet("/health", () => Results.Ok(new { message = "Service is up." }))
    .WithTags("Health APIs");
app.MapGet("/v1/health", () => Results.Ok(new { message = "Service is up." }))
    .WithTags("Health APIs");

app.MapPost("/v1/rerank", async (RerankRequest request, RerankerOrchestrator orchestrator, CancellationToken ct) =>
{
    var response = await orchestrator.RerankAsync(request, ct);
    return Results.Ok(response);
}).WithTags("Reranker APIs");

app.MapPost("/rerank", async (RerankRequest request, RerankerOrchestrator orchestrator, CancellationToken ct) =>
{
    var response = await orchestrator.RerankAsync(request, ct);
    return Results.Ok(response);
}).WithTags("Reranker APIs");

app.Run();
