using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Models;

namespace DotnetRag.Reranker.Providers;

public sealed class OllamaRerankerProvider(
    IHttpClientFactory httpClientFactory,
    RerankerServiceConfiguration config,
    ILogger<OllamaRerankerProvider> logger) : IRerankerProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public string Name => "ollama";

    public async Task<IReadOnlyList<RerankChunkResult>> RerankAsync(
        RerankRequest request,
        CancellationToken cancellationToken = default)
    {
        var useFallbackConfig = string.Equals(request.Provider, "fallback", StringComparison.OrdinalIgnoreCase);
        var baseUrl = ResolveBaseUrl(
            useFallbackConfig ? config.FallbackEndpoint : config.PrimaryEndpoint,
            useFallbackConfig ? config.PrimaryEndpoint : config.FallbackEndpoint);
        var model = ResolveModel(
            request.Model,
            useFallbackConfig ? config.FallbackModel : config.PrimaryModel,
            useFallbackConfig ? config.PrimaryModel : config.FallbackModel);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Ollama reranker endpoint is not configured.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("Ollama reranker model is not configured.");
        }

        var embedEndpoint = NormalizeEmbedEndpoint(baseUrl);
        var input = new List<string> { request.Query };
        input.AddRange(request.Chunks.Select(chunk => chunk.Text));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = input
        };

        var client = httpClientFactory.CreateClient("reranker-ollama");
        using var message = new HttpRequestMessage(HttpMethod.Post, embedEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };

        logger.LogDebug(
            "Calling Ollama reranker endpoint={Endpoint} model={Model} docs={Count}",
            embedEndpoint,
            model,
            request.Chunks.Count);

        using var response = await client.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Ollama reranker failed ({(int)response.StatusCode}): {body}");
        }

        var root = JsonNode.Parse(body)?.AsObject()
            ?? throw new InvalidOperationException("Ollama reranker returned empty body.");

        var embeddings = ExtractEmbeddings(root);
        if (embeddings.Count != request.Chunks.Count + 1)
        {
            throw new InvalidOperationException(
                $"Unexpected Ollama embeddings count={embeddings.Count}; expected={request.Chunks.Count + 1}.");
        }

        var queryVector = embeddings[0];
        var reranked = request.Chunks
            .Select((chunk, index) =>
            {
                var similarity = CosineSimilarity(queryVector, embeddings[index + 1]);
                return new RerankChunkResult(
                    Id: chunk.Id,
                    Text: chunk.Text,
                    OriginalScore: chunk.Score,
                    RelevanceScore: similarity,
                    Metadata: chunk.Metadata);
            })
            .OrderByDescending(item => item.RelevanceScore)
            .Take(request.TopK)
            .ToList();

        return reranked;
    }

    private static string ResolveBaseUrl(string primaryEndpoint, string fallbackEndpoint)
    {
        if (!string.IsNullOrWhiteSpace(primaryEndpoint))
        {
            return primaryEndpoint;
        }

        return fallbackEndpoint;
    }

    private static string ResolveModel(string? requestModel, string configuredPrimaryModel, string configuredFallbackModel)
    {
        if (!string.IsNullOrWhiteSpace(requestModel))
        {
            return requestModel;
        }

        if (!string.IsNullOrWhiteSpace(configuredPrimaryModel))
        {
            return configuredPrimaryModel;
        }

        return configuredFallbackModel;
    }

    private static string NormalizeEmbedEndpoint(string baseUrl)
    {
        var normalized = baseUrl.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"http://{normalized}";
        }

        normalized = normalized.TrimEnd('/');

        foreach (var suffix in new[]
        {
            "/v1/chat/completions",
            "/v1/embeddings",
            "/api/generate",
            "/api/chat",
            "/api/embed",
            "/api/embeddings",
            "/v1"
        })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }

        return $"{normalized}/api/embed";
    }

    private static List<IReadOnlyList<float>> ExtractEmbeddings(JsonObject root)
    {
        var embeddingsArray = root["embeddings"]?.AsArray();
        if (embeddingsArray is not null)
        {
            return embeddingsArray
                .Select(node => node?.AsArray()
                    .Select(value => value?.GetValue<float>() ?? 0f)
                    .ToList() as IReadOnlyList<float> ?? [])
                .ToList();
        }

        var singleEmbedding = root["embedding"]?.AsArray();
        if (singleEmbedding is not null)
        {
            return
            [
                singleEmbedding.Select(value => value?.GetValue<float>() ?? 0f).ToList()
            ];
        }

        throw new InvalidOperationException("Ollama reranker response missing embeddings.");
    }

    private static double CosineSimilarity(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        var length = Math.Min(a.Count, b.Count);
        if (length == 0)
        {
            return 0.0;
        }

        double dot = 0;
        double aNorm = 0;
        double bNorm = 0;

        for (var i = 0; i < length; i++)
        {
            dot += a[i] * b[i];
            aNorm += a[i] * a[i];
            bNorm += b[i] * b[i];
        }

        if (aNorm <= 0 || bNorm <= 0)
        {
            return 0.0;
        }

        return dot / (Math.Sqrt(aNorm) * Math.Sqrt(bNorm));
    }
}
