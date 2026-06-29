using System.Net.Http.Headers;
using System.Text.Json;
using DotnetRag.Ingestor.Models;

namespace DotnetRag.Ingestor.Services;

public sealed class HttpExternalIngestionPipeline(
    HttpClient httpClient,
    string backendName,
    Uri endpoint,
    string? apiKey) : IIngestionPipeline
{
    public string BackendName { get; } = backendName;
    public bool SupportsMultimodalExtraction => true;
    public bool SupportsObjectStoreAssets => true;

    public async Task<IngestionPipelineResult> ExtractAsync(
        string path,
        string filename,
        ExtractionOptions? extractionOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Document '{filename}' was not found.", path);
        }

        using var form = new MultipartFormDataContent();
        await using var stream = File.OpenRead(path);
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "document", filename);
        form.Add(new StringContent(BackendName), "backend");
        if (extractionOptions is not null)
        {
            form.Add(
                new StringContent(JsonSerializer.Serialize(
                    extractionOptions,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })),
                "extraction_options");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = form
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"External ingestion backend '{BackendName}' failed ({(int)response.StatusCode}): {body}");
        }

        return ParseResponse(body);
    }

    public async Task<string> ExtractTextAsync(
        string path,
        string filename,
        CancellationToken cancellationToken = default)
    {
        var result = await ExtractAsync(path, filename, cancellationToken: cancellationToken);
        return result.Text;
    }

    private static IngestionPipelineResult ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var text = GetString(root, "text")
            ?? GetString(root, "content")
            ?? GetString(root, "extracted_text")
            ?? GetString(root, "document_text")
            ?? GetString(root, "raw_text")
            ?? string.Empty;

        var documentInfo = new Dictionary<string, object?>();
        if (root.TryGetProperty("document_info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in info.EnumerateObject())
            {
                documentInfo[property.Name] = ConvertJsonValue(property.Value);
            }
        }

        var assets = new List<string>();
        if (root.TryGetProperty("asset_object_names", out var assetNames)
            && assetNames.ValueKind == JsonValueKind.Array)
        {
            assets.AddRange(assetNames.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>());
        }

        return new IngestionPipelineResult(text, documentInfo, assets);
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static object? ConvertJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var l) => l,
            JsonValueKind.Number when value.TryGetDouble(out var d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertJsonValue).ToList(),
            JsonValueKind.Object => value.EnumerateObject()
                .ToDictionary(property => property.Name, property => ConvertJsonValue(property.Value)),
            _ => value.ToString()
        };
    }
}
