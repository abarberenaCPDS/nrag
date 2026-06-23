using System.Text.Json;
using DotnetRag.Ingestor.Models;
using DotnetRag.Ingestor.Services;
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

builder.Services.AddSingleton<IngestionTaskHandler>();
builder.Services.AddSingleton<InMemoryIngestorStore>();
builder.Services.AddSingleton<IngestorService>();

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
    return Results.Ok(response);
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
    return Results.Ok(response);
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

        var response = service.CreateCollections(vdb_endpoint, collectionNames);
        return Results.Ok(response);
    });

app.MapPost("/collection", async (CreateCollectionRequest request, IngestorService service) =>
{
    var response = await service.CreateCollectionAsync(request);
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

app.Run();

file sealed class MultipartUploadParseResult(
    bool success,
    IResult? errorResult,
    IReadOnlyList<IFormFile>? files,
    DocumentUploadRequest? payload)
{
    public bool Success { get; } = success;
    public IResult? ErrorResult { get; } = errorResult;
    public IReadOnlyList<IFormFile>? Files { get; } = files;
    public DocumentUploadRequest? Payload { get; } = payload;
}

file static class MultipartRequestExtensions
{
    private static readonly JsonSerializerOptions SnakeCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static async Task<MultipartUploadParseResult> ReadMultipartPayloadAsync(
        this HttpRequest request)
    {
        if (!request.HasFormContentType)
        {
            return new MultipartUploadParseResult(
                false,
                Results.BadRequest(new { message = "Expected multipart/form-data payload." }),
                null,
                null);
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync();
        }
        catch
        {
            return new MultipartUploadParseResult(
                false,
                Results.BadRequest(new { message = "Unable to parse multipart form payload." }),
                null,
                null);
        }

        var files = form.Files.GetFiles("documents");
        if (files.Count == 0)
        {
            return new MultipartUploadParseResult(
                false,
                Results.BadRequest(new { message = "No files provided for uploading." }),
                null,
                null);
        }

        if (!form.TryGetValue("data", out var dataValues) || dataValues.Count == 0)
        {
            return new MultipartUploadParseResult(
                false,
                Results.BadRequest(new { message = "Form field 'data' is required." }),
                null,
                null);
        }

        var dataString = dataValues[0]?.ToString();
        if (string.IsNullOrWhiteSpace(dataString))
        {
            return new MultipartUploadParseResult(
                false,
                Results.UnprocessableEntity(new { message = "Invalid upload payload." }),
                null,
                null);
        }

        DocumentUploadRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<DocumentUploadRequest>(dataString, SnakeCaseOptions);
        }
        catch (JsonException)
        {
            return new MultipartUploadParseResult(
                false,
                Results.BadRequest(new { message = "Invalid JSON format in 'data' field." }),
                null,
                null);
        }

        if (payload is null)
        {
            return new MultipartUploadParseResult(
                false,
                Results.UnprocessableEntity(new { message = "Invalid upload payload." }),
                null,
                null);
        }

        return new MultipartUploadParseResult(true, null, files, payload);
    }
}
