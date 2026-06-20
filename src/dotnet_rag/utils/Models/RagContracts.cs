using System.Text.Json.Serialization;

namespace DotnetRag.Shared.Models;

public sealed record TextContent(
    [property: JsonPropertyName("type")] string Type = "text",
    [property: JsonPropertyName("text")] string Text = "");

public sealed record ImageUrl([property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("detail")] string? Detail = null);

public sealed record ImageContent(
    [property: JsonPropertyName("type")] string Type = "image_url",
    [property: JsonPropertyName("image_url")] ImageUrl? ImageUrl = null);

public sealed record Message(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] object Content);

public sealed record Prompt(
    IReadOnlyList<Message> Messages,
    bool UseKnowledgeBase = true,
    double? Temperature = null,
    double? TopP = null,
    int? MinTokens = null,
    bool IgnoreEos = true,
    int? MaxTokens = null,
    int? MinThinkingTokens = null,
    int? MaxThinkingTokens = null,
    IReadOnlyList<string>? Stop = null,
    int RerankerTopK = 10,
    int VdbTopK = 100,
    string? VdbEndpoint = null,
    IReadOnlyList<string>? CollectionNames = null,
    bool EnableQueryRewriting = false,
    bool EnableReranker = true,
    bool EnableGuardrails = false,
    bool EnableCitations = true,
    bool EnableVlmInference = false,
    bool EnableFilterGenerator = false,
    string? Model = null,
    string? LlmEndpoint = null,
    string? EmbeddingModel = null,
    string? EmbeddingEndpoint = null,
    string? RerankerModel = null,
    string? RerankerEndpoint = null,
    string? VlmModel = null,
    string? VlmEndpoint = null,
    double? VlmTemperature = null,
    double? VlmTopP = null,
    int? VlmMaxTokens = null,
    bool? VlmEnableThinking = null,
    int? VlmThinkingTokenBudget = null,
    bool? VlmFilterThinkingTokens = null,
    int? VlmMaxTotalImages = null,
    object? FilterExpr = null,
    double ConfidenceThreshold = 0,
    bool? Agentic = null,
    bool EnableStreaming = true);

public sealed record DocumentSearch(
    object Query,
    IReadOnlyList<Message>? Messages = null,
    int RerankerTopK = 10,
    int VdbTopK = 100,
    string? VdbEndpoint = null,
    IReadOnlyList<string>? CollectionNames = null,
    bool EnableQueryRewriting = false,
    bool EnableReranker = true,
    bool EnableFilterGenerator = false,
    string? EmbeddingModel = null,
    string? EmbeddingEndpoint = null,
    string? RerankerModel = null,
    string? RerankerEndpoint = null,
    object? FilterExpr = null,
    double ConfidenceThreshold = 0,
    bool EnableCitations = true);

public sealed record RagConfigurationDefaults(
    double? Temperature,
    double? TopP,
    int MaxTokens,
    int VdbTopK,
    int RerankerTopK,
    double ConfidenceThreshold);

public sealed record FeatureTogglesDefaults(
    bool EnableReranker,
    bool EnableCitations,
    bool EnableGuardrails,
    bool EnableQueryRewriting,
    bool EnableVlmInference,
    bool EnableFilterGenerator);

public sealed record ModelsDefaults(
    string LlmModel,
    string EmbeddingModel,
    string RerankerModel,
    string VlmModel);

public sealed record EndpointsDefaults(
    string LlmEndpoint,
    string EmbeddingEndpoint,
    string RerankerEndpoint,
    string VlmEndpoint,
    string VdbEndpoint);

public sealed record ConfigurationResponse(
    RagConfigurationDefaults RagConfiguration,
    FeatureTogglesDefaults FeatureToggles,
    ModelsDefaults Models,
    EndpointsDefaults Endpoints);

public sealed record RankingOptions(
    string Ranker = "auto",
    double ScoreThreshold = 0.0);

public abstract record OpenAiFilter;

public sealed record ComparisonFilter(
    string Key,
    string Type,
    object Value) : OpenAiFilter;

public sealed record CompoundFilter(
    string Type,
    IReadOnlyList<OpenAiFilter> Filters) : OpenAiFilter;

public sealed record VectorStoreSearchRequest(
    object Query,
    OpenAiFilter? Filters = null,
    int MaxNumResults = 10,
    RankingOptions? RankingOptions = null,
    bool RewriteQuery = false);

public sealed record VectorStoreSearchResultContent(
    string Type,
    string Text);

public sealed record VectorStoreSearchResultItem(
    string FileId,
    string Filename,
    double Score,
    IReadOnlyDictionary<string, object?> Attributes,
    IReadOnlyList<VectorStoreSearchResultContent> Content);

public sealed record VectorStoreSearchResponse(
    string Object,
    string SearchQuery,
    IReadOnlyList<VectorStoreSearchResultItem> Data,
    bool HasMore = false,
    string? NextPage = null);

public sealed record SummaryResponse(
    string Message = "",
    string Status = "",
    string Summary = "",
    string FileName = "",
    string CollectionName = "",
    string? Error = null,
    string? StartedAt = null,
    string? CompletedAt = null,
    string? UpdatedAt = null,
    IReadOnlyDictionary<string, object?>? Progress = null);

public sealed record ChainResponse(
    string Content,
    string? Reasoning = null,
    IReadOnlyDictionary<string, object?>? Usage = null);

public sealed record Citations(
    IReadOnlyList<VectorStoreSearchResultItem> Results,
    string Message = "");
