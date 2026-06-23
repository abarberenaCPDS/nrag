using System.Text.Json.Serialization;

namespace DotnetRag.Shared.Models;

public sealed record RerankChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record RerankRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("chunks")] IReadOnlyList<RerankChunk> Chunks,
    [property: JsonPropertyName("top_k")] int TopK = 10,
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("return_all")] bool ReturnAll = false);

public sealed record RerankChunkResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("original_score")] double OriginalScore,
    [property: JsonPropertyName("relevance_score")] double RelevanceScore,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record RerankResponse(
    [property: JsonPropertyName("results")] IReadOnlyList<RerankChunkResult> Results,
    [property: JsonPropertyName("provider_used")] string ProviderUsed,
    [property: JsonPropertyName("fallback_used")] bool FallbackUsed = false,
    [property: JsonPropertyName("message")] string? Message = null);
