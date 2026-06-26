using System.Text.Json;
using dotnet_rag.ingestor_server;
using DotnetRag.Ingestor.Models;
using DotnetRag.Ingestor.Services;
using DotnetRag.Ingestor.Services.ObjectStore;
using DotnetRag.Ingestor.Services.Telemetry;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Extensions;
using DotnetRag.Shared.Options;
using Microsoft.OpenApi.Models;

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
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Dotnet Ingestor API",
        Version = "v1",
        Description = "OpenAPI specification for the .NET ingestor server."
    });
});

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

// Register Ollama + ChromaDB infrastructure shared with rag_server
builder.Services.AddRagInfrastructure(ragConfig);

if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APP_INGESTION_TASK_STORE_PATH")))
{
    builder.Services.AddSingleton<IIngestionTaskStore, FileBackedIngestionTaskStore>();
}
else
{
    builder.Services.AddSingleton<IIngestionTaskStore, InMemoryIngestionTaskStore>();
}

if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APP_INGESTION_JOB_QUEUE_PATH")))
{
    builder.Services.AddSingleton<IIngestionJobQueue, FileBackedIngestionJobQueue>();
}
else
{
    builder.Services.AddSingleton<IIngestionJobQueue, InMemoryIngestionJobQueue>();
}

builder.Services.AddSingleton<IngestionTaskHandler>();
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APP_INGESTOR_CATALOG_PATH")))
{
    builder.Services.AddSingleton<IIngestorCatalogStore, FileBackedIngestorCatalogStore>();
}
else
{
    builder.Services.AddSingleton<IIngestorCatalogStore, DisabledIngestorCatalogStore>();
}
builder.Services.AddSingleton<InMemoryIngestorStore>();
builder.Services.AddSingleton<IIngestionPipeline>(sp =>
    IngestionPipelineFactory.Create(sp.GetRequiredService<IHttpClientFactory>()));
builder.Services.AddSingleton<IIngestionTelemetrySink, LoggingIngestionTelemetrySink>();
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APP_OBJECT_STORE_ROOT")))
{
    builder.Services.AddSingleton<IObjectStoreService, FileObjectStoreService>();
}
else
{
    builder.Services.AddSingleton<IObjectStoreService, DisabledObjectStoreService>();
}
builder.Services.AddSingleton<IObjectStore>(sp => sp.GetRequiredService<IObjectStoreService>());
builder.Services.AddSingleton<IngestorService>();
if (IsWorkerRole())
{
    builder.Services.AddHostedService<IngestionWorkerService>();
}

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Dotnet Ingestor API v1");
    options.RoutePrefix = "swagger";
});

app.MapGet("/health",async (IngestorService service, bool check_dependencies = false) =>
        Results.Ok(await service.HealthAsync(check_dependencies))
);

app.MapGet("/v1/health", async (IngestorService service, bool check_dependencies = false) =>
    Results.Ok(await service.HealthAsync(check_dependencies))
);

app.MapPost("/documents", async (HttpRequest request, IngestorService service) =>
{
    var parsed = await request.ReadMultipartPayloadAsync();
    if (!parsed.Success)
    {
        return parsed.ErrorResult!;
    }

    var response = await service.UploadDocumentsAsync(
        request,
        parsed.Files!,
        parsed.Payload!,
        isUpdate: false);
    return ToUploadResult(response);
});

app.MapPatch("/documents", async (HttpRequest request, IngestorService service) =>
{
    var parsed = await request.ReadMultipartPayloadAsync();
    if (!parsed.Success)
    {
        return parsed.ErrorResult!;
    }

    var response = await service.UploadDocumentsAsync(
        request,
        parsed.Files!,
        parsed.Payload!,
        isUpdate: true);
    return ToUploadResult(response);
});

app.MapGet("/status", async (string task_id, IngestorService service) =>
    Results.Ok(await service.GetTaskStatusAsync(task_id)));

app.MapGet(
    "/documents",
    (HttpRequest request,
        IngestorService service,
        string? collection_name,
        string? vdb_endpoint,
        bool force_get_metadata,
        int max_results = 1000) =>
    {
        var response = service.GetDocuments(
            request,
            collection_name,
            vdb_endpoint,
            force_get_metadata,
            max_results);
        return Results.Ok(response);
    });

app.MapDelete(
    "/documents",
    async (HttpRequest request,
        IngestorService service,
        string? collection_name,
        string? vdb_endpoint) =>
    {
        var documentNames = await request.ReadFromJsonAsync<List<string>>() ?? [];
        var response = await service.DeleteDocumentsAsync(
            request,
            documentNames,
            collection_name,
            vdb_endpoint);
        return Results.Ok(response);
    });

app.MapGet(
    "/collections",
    (HttpRequest request, IngestorService service, string? vdb_endpoint) =>
    {
        var response = service.GetCollections(request, vdb_endpoint);
        return Results.Ok(response);
    });

app.MapPost(
    "/collections",
    async (HttpRequest request, IngestorService service, string? vdb_endpoint) =>
    {
        var collectionNames = await request.ReadFromJsonAsync<List<string>>() ?? [];
        if (collectionNames.Count == 0)
        {
            collectionNames = [service.DefaultCollectionName];
        }

        var response = service.CreateCollections(request, vdb_endpoint, collectionNames);
        return Results.Ok(response);
    });

app.MapPost("/collection", async (HttpRequest httpRequest, CreateCollectionRequest request, IngestorService service) =>
{
    var response = await service.CreateCollectionAsync(httpRequest, request);
    return Results.Ok(response);
});

app.MapPatch(
    "/collections/{collection_name}/metadata",
    (string collection_name, UpdateCollectionMetadataRequest request, IngestorService service) =>
    {
        var response = service.UpdateCollectionMetadata(collection_name, request);
        return Results.Ok(response);
    });

app.MapPatch(
    "/collections/{collection_name}/documents/{document_name}/metadata",
    (string collection_name,
        string document_name,
        UpdateDocumentMetadataRequest request,
        IngestorService service) =>
    {
        var response = service.UpdateDocumentMetadata(collection_name, document_name, request);
        return Results.Ok(response);
    });

app.MapDelete(
    "/collections",
    async (HttpRequest request, IngestorService service, string? vdb_endpoint) =>
    {
        var collectionNames = await request.ReadFromJsonAsync<List<string>>() ?? [];
        if (collectionNames.Count == 0)
        {
            collectionNames = [service.DefaultCollectionName];
        }

        var response = await service.DeleteCollectionsAsync(request, collectionNames, vdb_endpoint);
        return Results.Ok(response);
    });

static IResult ToUploadResult(object response) =>
    response is UploadValidationErrorResponse
        ? Results.UnprocessableEntity(response)
        : Results.Ok(response);

static bool IsWorkerRole()
{
    var role = Environment.GetEnvironmentVariable("APP_INGESTOR_ROLE");
    return string.IsNullOrWhiteSpace(role)
        || string.Equals(role, "all", StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, "worker", StringComparison.OrdinalIgnoreCase);
}

await app.RunAsync();
