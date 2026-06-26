using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetRag.Shared.Abstractions;
using Microsoft.Extensions.Logging;

namespace DotnetRag.Shared.Embedding;

/// <summary>
/// Generates text embeddings via the Ollama /api/embed endpoint.
/// ORIG_EMBED_MODEL: nvidia/llama-nemotron-embed-vl-1b-v2 (NIM endpoint)
/// Default local model: nomic-embed-text (768 dims, best general-purpose)
/// </summary>
public sealed class OllamaEmbeddingService : IEmbeddingService
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _endpoint;
    private readonly ILogger<OllamaEmbeddingService> _logger;

    public OllamaEmbeddingService(
        IHttpClientFactory httpClientFactory,
        string model,
        string ollamaBaseUrl,
        ILogger<OllamaEmbeddingService> logger)
    {
        _http = httpClientFactory.CreateClient("ollama");
        // ORIG_EMBED_ENDPOINT: nemotron-vlm-embedding-ms:8000/v1
        _endpoint = ollamaBaseUrl.TrimEnd('/') + "/api/embed";
        _model = model;
        _logger = logger;
    }

    public async Task<IReadOnlyList<float>> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var payload = new { model = _model, input = text };
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync(_endpoint, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Ollama /api/embed call failed ({StatusCode}): {Body}",
                (int)response.StatusCode,
                body);
            response.EnsureSuccessStatusCode();
        }

        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var embeddings = root?["embeddings"]?.AsArray();
        if (embeddings is not null && embeddings.Count > 0 && embeddings[0] is JsonArray firstEmbedding)
        {
            return firstEmbedding.Select(n => n?.GetValue<float>() ?? 0f).ToList();
        }

        // Keep accepting the legacy /api/embeddings response shape if an older server or proxy returns it.
        var legacyEmbedding = root?["embedding"]?.AsArray();
        if (legacyEmbedding is not null)
        {
            return legacyEmbedding.Select(n => n?.GetValue<float>() ?? 0f).ToList();
        }

        throw new InvalidOperationException("Ollama /api/embed did not return an 'embeddings' array.");
    }
}
