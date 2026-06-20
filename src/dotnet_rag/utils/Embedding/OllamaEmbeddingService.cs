using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetRag.Shared.Abstractions;
using Microsoft.Extensions.Logging;

namespace DotnetRag.Shared.Embedding;

/// <summary>
/// Generates text embeddings via the Ollama /api/embeddings endpoint.
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
        _endpoint = ollamaBaseUrl.TrimEnd('/') + "/api/embeddings";
        _model = model;
        _logger = logger;
    }

    public async Task<IReadOnlyList<float>> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var payload = new { model = _model, prompt = text };
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync(_endpoint, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Ollama embeddings call failed ({StatusCode}): {Body}",
                (int)response.StatusCode,
                body);
            response.EnsureSuccessStatusCode();
        }

        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var embedding = root?["embedding"]?.AsArray();
        if (embedding is null)
        {
            throw new InvalidOperationException("Ollama /api/embeddings did not return an 'embedding' array.");
        }

        return embedding.Select(n => n?.GetValue<float>() ?? 0f).ToList();
    }
}
