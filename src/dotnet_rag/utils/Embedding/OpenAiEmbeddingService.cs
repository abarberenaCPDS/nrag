using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetRag.Shared.Abstractions;
using Microsoft.Extensions.Logging;

namespace DotnetRag.Shared.Embedding;

public sealed class OpenAiEmbeddingService : IEmbeddingService
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _endpoint;
    private readonly string? _apiKey;
    private readonly ILogger<OpenAiEmbeddingService> _logger;

    public OpenAiEmbeddingService(
        IHttpClientFactory httpClientFactory,
        string model,
        string baseUrl,
        string? apiKey,
        ILogger<OpenAiEmbeddingService> logger)
    {
        _http = httpClientFactory.CreateClient("openai");
        _model = model;
        _endpoint = NormalizeEmbeddingsEndpoint(baseUrl);
        _apiKey = apiKey;
        _logger = logger;
    }

    public async Task<IReadOnlyList<float>> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new
                    {
                        model = _model,
                        input = text
                    },
                    JsonOpts),
                Encoding.UTF8,
                "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "OpenAI-compatible embeddings call failed ({StatusCode}): {Body}",
                (int)response.StatusCode,
                body);
            response.EnsureSuccessStatusCode();
        }

        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var embedding = root?["data"]?[0]?["embedding"]?.AsArray()
            ?? throw new InvalidOperationException(
                "OpenAI-compatible embeddings response did not include data[0].embedding.");

        return embedding.Select(node => node?.GetValue<float>() ?? 0f).ToList();
    }

    private static string NormalizeEmbeddingsEndpoint(string url)
    {
        var normalized = url.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"http://{normalized}";
        }

        normalized = normalized.TrimEnd('/');
        if (normalized.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return $"{normalized}/embeddings";
        }

        return $"{normalized}/v1/embeddings";
    }
}
