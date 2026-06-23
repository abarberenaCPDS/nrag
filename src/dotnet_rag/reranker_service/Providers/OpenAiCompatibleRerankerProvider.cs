using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Models;

namespace DotnetRag.Reranker.Providers;

public sealed class OpenAiCompatibleRerankerProvider(
    IHttpClientFactory httpClientFactory,
    RerankerServiceConfiguration config,
    ILogger<OpenAiCompatibleRerankerProvider> logger) : IRerankerProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public string Name => "openai";

    public async Task<IReadOnlyList<RerankChunkResult>> RerankAsync(
        RerankRequest request,
        CancellationToken cancellationToken = default)
    {
        var useFallbackConfig = string.Equals(request.Provider, "fallback", StringComparison.OrdinalIgnoreCase);
        var endpoint = NormalizeEndpoint(
            useFallbackConfig ? config.FallbackEndpoint : config.PrimaryEndpoint,
            useFallbackConfig ? config.PrimaryEndpoint : config.FallbackEndpoint);
        var model = ResolveModel(
            request.Model,
            useFallbackConfig ? config.FallbackModel : config.PrimaryModel,
            useFallbackConfig ? config.PrimaryModel : config.FallbackModel);
        var apiKey = useFallbackConfig
            ? (config.FallbackApiKey ?? config.PrimaryApiKey)
            : (config.PrimaryApiKey ?? config.FallbackApiKey);

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("OpenAI-compatible reranker endpoint is not configured.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("OpenAI-compatible reranker model is not configured.");
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["query"] = request.Query,
            ["documents"] = request.Chunks.Select(chunk => new { id = chunk.Id, text = chunk.Text }).ToList(),
            ["top_n"] = request.TopK,
            ["return_documents"] = true
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var client = httpClientFactory.CreateClient("reranker-openai");

        logger.LogDebug(
            "Calling OpenAI-compatible reranker endpoint={Endpoint} model={Model} docs={Count}",
            endpoint,
            model,
            request.Chunks.Count);

        using var response = await client.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OpenAI-compatible reranker failed ({(int)response.StatusCode}): {body}");
        }

        var root = JsonNode.Parse(body)?.AsObject()
            ?? throw new InvalidOperationException("OpenAI-compatible reranker returned empty body.");

        var resultNodes = root["results"]?.AsArray() ?? root["data"]?.AsArray()
            ?? throw new InvalidOperationException("OpenAI-compatible reranker response missing results/data array.");

        var chunkById = request.Chunks.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var ranked = new List<RerankChunkResult>(resultNodes.Count);

        foreach (var node in resultNodes)
        {
            var id = node?["document"]?["id"]?.GetValue<string>()
                ?? node?["id"]?.GetValue<string>()
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(id))
            {
                var index = node?["index"]?.GetValue<int?>();
                if (index is int i && i >= 0 && i < request.Chunks.Count)
                {
                    id = request.Chunks[i].Id;
                }
            }

            if (!chunkById.TryGetValue(id, out var chunk))
            {
                continue;
            }

            var relevance = node?["relevance_score"]?.GetValue<double?>()
                ?? node?["score"]?.GetValue<double?>()
                ?? chunk.Score;

            ranked.Add(new RerankChunkResult(
                Id: chunk.Id,
                Text: chunk.Text,
                OriginalScore: chunk.Score,
                RelevanceScore: relevance,
                Metadata: chunk.Metadata));
        }

        return ranked
            .OrderByDescending(item => item.RelevanceScore)
            .Take(request.TopK)
            .ToList();
    }

    private static string NormalizeEndpoint(string primaryEndpoint, string fallbackEndpoint)
    {
        var baseUrl = string.IsNullOrWhiteSpace(primaryEndpoint) ? fallbackEndpoint : primaryEndpoint;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        var normalized = baseUrl.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"http://{normalized}";
        }

        normalized = normalized.TrimEnd('/');

        if (normalized.EndsWith("/v1/rerank", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/rerank", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return $"{normalized}/rerank";
        }

        return $"{normalized}/v1/rerank";
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
}
