using System.Net.Http.Json;
using System.Text.Json;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Models;

namespace DotnetRag.Rag.Clients;

public sealed class HttpRerankerClient(
    IHttpClientFactory httpClientFactory,
    RagServerConfiguration config,
    ILogger<HttpRerankerClient> logger) : IRerankerClient
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<VectorSearchResult>> RerankAsync(
        string query,
        IReadOnlyList<VectorSearchResult> candidates,
        int topK,
        CancellationToken cancellationToken = default,
        string? endpoint = null)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var request = new RerankRequest(
            Query: query,
            Chunks: candidates.Select(chunk => new RerankChunk(
                Id: chunk.Id,
                Text: chunk.Text,
                Score: chunk.Score,
                Metadata: chunk.Metadata?.ToDictionary(kv => kv.Key, kv => kv.Value)))
            .ToList(),
            TopK: topK);

        var client = httpClientFactory.CreateClient("reranker");
        var rerankerEndpoint = BuildRerankerEndpoint(
            string.IsNullOrWhiteSpace(endpoint) ? config.RerankerServiceUrl : endpoint);

        logger.LogDebug("Calling reranker-service endpoint={Endpoint} chunks={Count}", rerankerEndpoint, candidates.Count);

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(rerankerEndpoint, request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new HttpRequestException(
                $"Reranker NIM unavailable at {NormalizeServiceUrl(string.IsNullOrWhiteSpace(endpoint) ? config.RerankerServiceUrl : endpoint)}. Please verify the service is running and accessible.",
                ex);
        }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Reranker service failed ({(int)response.StatusCode}): {responseBody}");
            }

            var parsed = JsonSerializer.Deserialize<RerankResponse>(responseBody, JsonOptions)
                ?? throw new InvalidOperationException("Reranker service returned an empty response.");

            return parsed.Results.Select(item => new VectorSearchResult(
                Id: item.Id,
                Text: item.Text,
                Score: item.RelevanceScore,
                Metadata: item.Metadata)).ToList();
        }
    }

    private static string BuildRerankerEndpoint(string serviceUrl)
    {
        var normalized = serviceUrl.Trim();
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

        return $"{normalized}/v1/rerank";
    }

    private static string NormalizeServiceUrl(string serviceUrl)
    {
        var normalized = serviceUrl.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"http://{normalized}";
        }

        return normalized.TrimEnd('/');
    }
}
