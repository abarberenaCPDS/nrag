using DotnetRag.Rag.Clients;
using DotnetRag.Rag.Observability;
using DotnetRag.Rag.Services;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Extensions;
using DotnetRag.Shared.Models;
using DotnetRag.Shared.Options;
using Microsoft.OpenApi.Models;
using System.Text.Json;

DotnetRagEnvironmentBootstrap.LoadSharedLocalEnvironment();

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddOptions<ModelOptions>()
    .Bind(builder.Configuration.GetSection("Models"));
builder.Services.AddOptions<TelemetryOptions>()
    .Bind(builder.Configuration.GetSection("Telemetry"));

var ragConfig = RagServerConfiguration.FromEnvironment();
builder.Services.AddSingleton(ragConfig);

builder.Logging.SetMinimumLevel(ragConfig.LogLevel.ToUpperInvariant() switch
{
    "DEBUG" or "NOTSET" => LogLevel.Debug,
    "WARNING" or "WARN" => LogLevel.Warning,
    "ERROR" => LogLevel.Error,
    "CRITICAL" => LogLevel.Critical,
    _ => LogLevel.Information
});

// Register Ollama + ChromaDB/Milvus infrastructure (LLM, embeddings, vector store)
builder.Services.AddRagInfrastructure(ragConfig);
builder.Services.AddHttpClient("reranker", client =>
{
    client.Timeout = TimeSpan.FromSeconds(ragConfig.RerankerServiceTimeoutSeconds);
});
builder.Services.AddSingleton<IRerankerClient, HttpRerankerClient>();

builder.Services.AddSingleton<QueryRewritingService>();
builder.Services.AddSingleton<ReflectionService>();
builder.Services.AddSingleton<FilterExpressionService>();
builder.Services.AddSingleton<RagMetrics>();
builder.Services.AddSingleton<RagService>();

// Observability: OpenTelemetry tracing + metrics, Prometheus scrape endpoint at /metrics
builder.Services.AddRagObservability(ragConfig);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "APIs for RAG Server (v1)",
        Version = "1.0.0",
        Description = "This API schema describes all the retriever endpoints exposed for RAG server Blueprint."
    });
    options.SwaggerDoc("v2", new OpenApiInfo
    {
        Title = "APIs for RAG Server (v2) - OpenAI Compatible",
        Version = "2.0.0",
        Description = "OpenAI-compatible API endpoints for RAG server Blueprint."
    });
});

var app = builder.Build();

// Prometheus scrape endpoint — serves real metrics in Prometheus text format
app.UseOpenTelemetryPrometheusScrapingEndpoint("/metrics");

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "swagger";
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "RAG API v1");
    options.SwaggerEndpoint("/swagger/v2/swagger.json", "RAG API v2");
});

app.MapGet("/docs", () => Results.Redirect("/swagger"));
app.MapGet("/openapi.json", () => Results.Redirect("/swagger/v1/swagger.json"));
app.MapGet("/v1/docs", () => Results.Redirect("/swagger"));
app.MapGet("/v1/openapi.json", () => Results.Redirect("/swagger/v1/swagger.json"));
app.MapGet("/v2/docs", () => Results.Redirect("/swagger"));
app.MapGet("/v2/openapi.json", () => Results.Redirect("/swagger/v2/swagger.json"));

app.MapGet("/health", async (RagService service, bool check_dependencies = false) =>
    Results.Ok(await service.HealthAsync(check_dependencies)))
    .WithTags("Health APIs");
app.MapGet("/v1/health", async (RagService service, bool check_dependencies = false) =>
    Results.Ok(await service.HealthAsync(check_dependencies)))
    .WithTags("Health APIs");

app.MapGet("/configuration", (RagService service) => Results.Ok(service.GetConfiguration()))
    .WithTags("Health APIs");
app.MapGet("/v1/configuration", (RagService service) => Results.Ok(service.GetConfiguration()))
    .WithTags("Health APIs");

// /v1/metrics redirects to the OTel Prometheus scrape endpoint
app.MapGet("/v1/metrics", () => Results.Redirect("/metrics"))
    .WithTags("Health APIs");

app.MapPost("/generate", async (HttpRequest request, Prompt prompt, RagService service) =>
    await service.GenerateAsync(request, prompt))
    .WithTags("RAG APIs");
app.MapPost("/v1/generate", async (HttpRequest request, Prompt prompt, RagService service) =>
    await service.GenerateAsync(request, prompt))
    .WithTags("RAG APIs");

app.MapPost("/chat/completions", async (HttpRequest request, Prompt prompt, RagService service) =>
    await service.GenerateAsync(request, prompt))
    .WithTags("RAG APIs");
app.MapPost("/v1/chat/completions", async (HttpRequest request, Prompt prompt, RagService service) =>
    await service.GenerateAsync(request, prompt))
    .WithTags("RAG APIs");

app.MapPost("/search", async (HttpRequest request, DocumentSearch data, RagService service) =>
    await service.SearchAsync(request, data))
    .WithTags("Retrieval APIs");
app.MapPost("/v1/search", async (HttpRequest request, DocumentSearch data, RagService service) =>
    await service.SearchAsync(request, data))
    .WithTags("Retrieval APIs");

app.MapPost("/v2/vector_stores/{vector_store_id}/search",
    async (HttpRequest request, string vector_store_id, VectorStoreSearchRequest search_request, RagService service) =>
        await service.VectorStoreSearchAsync(request, vector_store_id, search_request))
    .WithTags("Retrieval APIs");

app.MapGet("/summary",
    async (HttpRequest request, string collection_name, string file_name, RagService service, bool blocking = false, double timeout = 300) =>
        await service.GetSummaryAsync(request, collection_name, file_name, blocking, timeout))
    .WithTags("Retrieval APIs");
app.MapGet("/v1/summary",
    async (HttpRequest request, string collection_name, string file_name, RagService service, bool blocking = false, double timeout = 300) =>
        await service.GetSummaryAsync(request, collection_name, file_name, blocking, timeout))
    .WithTags("Retrieval APIs");

await app.RunAsync();
