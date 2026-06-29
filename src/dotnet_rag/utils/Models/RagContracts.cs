using System.Text.Json;
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
    bool EnableQueryDecomposition = false,
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
    string? QueryRewriterModel = null,
    string? QueryRewriterEndpoint = null,
    string? QueryRewriterApiKey = null,
    string? FilterExpressionGeneratorModel = null,
    string? FilterExpressionGeneratorEndpoint = null,
    string? FilterExpressionGeneratorApiKey = null,
    string? ReflectionModel = null,
    string? ReflectionEndpoint = null,
    string? ReflectionApiKey = null,
    double? VlmTemperature = null,
    double? VlmTopP = null,
    int? VlmMaxTokens = null,
    bool? VlmEnableThinking = null,
    int? VlmThinkingTokenBudget = null,
    bool? VlmFilterThinkingTokens = null,
    int? VlmMaxTotalImages = null,
    object? FilterExpr = null,
    double? ConfidenceThreshold = null,
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
    string? QueryRewriterModel = null,
    string? QueryRewriterEndpoint = null,
    string? QueryRewriterApiKey = null,
    string? FilterExpressionGeneratorModel = null,
    string? FilterExpressionGeneratorEndpoint = null,
    string? FilterExpressionGeneratorApiKey = null,
    object? FilterExpr = null,
    double? ConfidenceThreshold = null,
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
    bool EnableQueryDecomposition,
    bool EnableVlmInference,
    bool EnableFilterGenerator,
    bool EnableAgenticRag);

public sealed record ModelsDefaults(
    string LlmModel,
    string EmbeddingModel,
    string RerankerModel,
    string VlmModel,
    string QueryRewriterModel,
    string FilterExpressionGeneratorModel,
    string ReflectionModel);

public sealed record EndpointsDefaults(
    string LlmEndpoint,
    string EmbeddingEndpoint,
    string RerankerEndpoint,
    string VlmEndpoint,
    string VdbEndpoint,
    string QueryRewriterEndpoint,
    string FilterExpressionGeneratorEndpoint,
    string ReflectionEndpoint);

public sealed record ProvidersDefaults(
    string LlmProvider,
    string EmbeddingProvider,
    string VlmProvider,
    string VectorStoreProvider);

public sealed record ConfigurationResponse(
    RagConfigurationDefaults RagConfiguration,
    FeatureTogglesDefaults FeatureToggles,
    ModelsDefaults Models,
    EndpointsDefaults Endpoints,
    ProvidersDefaults Providers);

public sealed record RankingOptions(
    string Ranker = "auto",
    double ScoreThreshold = 0.0);

[JsonConverter(typeof(OpenAiFilterJsonConverter))]
public abstract record OpenAiFilter;

public sealed record ComparisonFilter(
    string Key,
    string Type,
    object Value) : OpenAiFilter;

public sealed record CompoundFilter(
    string Type,
    IReadOnlyList<OpenAiFilter> Filters) : OpenAiFilter;

public sealed class OpenAiFilterJsonConverter : JsonConverter<OpenAiFilter>
{
    public override OpenAiFilter? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("OpenAI vector-store filter must be an object.");
        }

        var type = root.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString() ?? string.Empty
            : string.Empty;

        if (root.TryGetProperty("filters", out var filtersElement))
        {
            if (filtersElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Compound filter 'filters' must be an array.");
            }

            var filters = filtersElement
                .EnumerateArray()
                .Select(element => JsonSerializer.Deserialize<OpenAiFilter>(
                    element.GetRawText(),
                    options))
                .Where(filter => filter is not null)
                .Cast<OpenAiFilter>()
                .ToList();

            return new CompoundFilter(type, filters);
        }

        if (!root.TryGetProperty("key", out var keyElement)
            || !root.TryGetProperty("value", out var valueElement))
        {
            throw new JsonException("Comparison filter must include 'key' and 'value'.");
        }

        return new ComparisonFilter(
            keyElement.GetString() ?? string.Empty,
            type,
            valueElement.Clone());
    }

    public override void Write(
        Utf8JsonWriter writer,
        OpenAiFilter value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case ComparisonFilter comparison:
                writer.WriteString("type", comparison.Type);
                writer.WriteString("key", comparison.Key);
                writer.WritePropertyName("value");
                JsonSerializer.Serialize(writer, comparison.Value, options);
                break;
            case CompoundFilter compound:
                writer.WriteString("type", compound.Type);
                writer.WritePropertyName("filters");
                JsonSerializer.Serialize(writer, compound.Filters, options);
                break;
            default:
                throw new JsonException($"Unsupported filter type '{value.GetType()}'.");
        }
        writer.WriteEndObject();
    }
}

public sealed record VectorStoreSearchRequest(
    object Query,
    OpenAiFilter? Filters = null,
    int MaxNumResults = 10,
    RankingOptions? RankingOptions = null,
    bool RewriteQuery = false,
    string? VdbEndpoint = null,
    string? EmbeddingModel = null,
    string? EmbeddingEndpoint = null,
    string? QueryRewriterModel = null,
    string? QueryRewriterEndpoint = null,
    string? QueryRewriterApiKey = null,
    string? FilterExpressionGeneratorModel = null,
    string? FilterExpressionGeneratorEndpoint = null,
    string? FilterExpressionGeneratorApiKey = null,
    string? RerankerEndpoint = null);

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
