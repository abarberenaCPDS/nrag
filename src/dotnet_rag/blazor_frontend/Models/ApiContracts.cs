using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetRag.Blazor.Models;

// ── Chat / Generation ─────────────────────────────────────────────────────────

public sealed record ChatMessage(string Id, string Role, string Content)
{
    public string Content { get; set; } = Content;
    public List<Citation> Citations { get; set; } = [];
    public List<ReasoningStep> ReasoningSteps { get; set; } = [];
    public bool IsStreaming { get; set; }
    public bool IsError { get; set; }
    public List<AttachedImage> Images { get; set; } = [];
}

public sealed record AttachedImage(string DataUri, string MediaType);

public sealed class ReasoningStep
{
    public string Stage { get; init; } = "";
    public string? Label { get; set; }
    public string Reasoning { get; set; } = "";
    public string Output { get; set; } = "";
    public string Status { get; set; } = "running"; // "running" | "done" | "error"
    public string? Summary { get; set; }
}

// ── SSE Wire Format ───────────────────────────────────────────────────────────

public sealed class StreamChunk
{
    [JsonPropertyName("choices")]
    public List<StreamChoice>? Choices { get; set; }

    [JsonPropertyName("citations")]
    public CitationsWrapper? Citations { get; set; }

    [JsonPropertyName("event_type")]
    public string? EventType { get; set; }

    [JsonPropertyName("stage")]
    public string? Stage { get; set; }
}

public sealed class StreamChoice
{
    [JsonPropertyName("delta")]
    public StreamDelta? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public sealed class StreamDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }
    // event_type and stage are top-level ChainResponse fields only — not in Message/delta
}

// ── Request Types ─────────────────────────────────────────────────────────────

public sealed class GenerateRequest
{
    [JsonPropertyName("messages")]
    public List<MessagePayload> Messages { get; set; } = [];

    [JsonPropertyName("use_knowledge_base")]
    public bool UseKnowledgeBase { get; set; } = true;

    [JsonPropertyName("enable_streaming")]
    public bool EnableStreaming { get; set; } = true;

    [JsonPropertyName("collection_names")]
    public List<string>? CollectionNames { get; set; }

    [JsonPropertyName("filter_expr")]
    public string? FilterExpr { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("vdb_top_k")]
    public int? VdbTopK { get; set; }

    [JsonPropertyName("reranker_top_k")]
    public int? RerankerTopK { get; set; }

    [JsonPropertyName("enable_reranker")]
    public bool? EnableReranker { get; set; }

    [JsonPropertyName("enable_citations")]
    public bool? EnableCitations { get; set; }

    [JsonPropertyName("enable_query_rewriting")]
    public bool? EnableQueryRewriting { get; set; }

    [JsonPropertyName("enable_query_decomposition")]
    public bool? EnableQueryDecomposition { get; set; }

    [JsonPropertyName("enable_guardrails")]
    public bool? EnableGuardrails { get; set; }

    [JsonPropertyName("enable_vlm_inference")]
    public bool? EnableVlmInference { get; set; }

    [JsonPropertyName("enable_filter_generator")]
    public bool? EnableFilterGenerator { get; set; }

    [JsonPropertyName("agentic")]
    public bool Agentic { get; set; }

    [JsonPropertyName("confidence_threshold")]
    public double? ConfidenceThreshold { get; set; }

    [JsonPropertyName("vdb_endpoint")]
    public string? VdbEndpoint { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("embedding_model")]
    public string? EmbeddingModel { get; set; }

    [JsonPropertyName("reranker_model")]
    public string? RerankerModel { get; set; }

    [JsonPropertyName("vlm_model")]
    public string? VlmModel { get; set; }

    [JsonPropertyName("llm_endpoint")]
    public string? LlmEndpoint { get; set; }

    [JsonPropertyName("embedding_endpoint")]
    public string? EmbeddingEndpoint { get; set; }

    [JsonPropertyName("reranker_endpoint")]
    public string? RerankerEndpoint { get; set; }

    [JsonPropertyName("vlm_endpoint")]
    public string? VlmEndpoint { get; set; }

    [JsonPropertyName("vlm_enable_thinking")]
    public bool? VlmEnableThinking { get; set; }

    [JsonPropertyName("vlm_thinking_token_budget")]
    public int? VlmThinkingTokenBudget { get; set; }

    [JsonPropertyName("vlm_filter_thinking_tokens")]
    public bool? VlmFilterThinkingTokens { get; set; }

    [JsonPropertyName("query_rewriter_model")]
    public string? QueryRewriterModel { get; set; }

    [JsonPropertyName("query_rewriter_endpoint")]
    public string? QueryRewriterEndpoint { get; set; }

    [JsonPropertyName("filter_expression_generator_model")]
    public string? FilterExpressionGeneratorModel { get; set; }

    [JsonPropertyName("filter_expression_generator_endpoint")]
    public string? FilterExpressionGeneratorEndpoint { get; set; }

    [JsonPropertyName("reflection_model")]
    public string? ReflectionModel { get; set; }

    [JsonPropertyName("reflection_endpoint")]
    public string? ReflectionEndpoint { get; set; }

    [JsonPropertyName("stop")]
    public List<string>? Stop { get; set; }
}

public sealed class MessagePayload
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public object Content { get; set; } = "";
}

// ── Citations ─────────────────────────────────────────────────────────────────

public sealed class CitationsWrapper
{
    [JsonPropertyName("total_results")]
    public int TotalResults { get; set; }

    [JsonPropertyName("results")]
    public List<Citation> Results { get; set; } = [];
}

public sealed class Citation
{
    [JsonPropertyName("document_id")]
    public string? DocumentId { get; set; }

    [JsonPropertyName("document_name")]
    public string? DocumentName { get; set; }

    [JsonPropertyName("collection_name")]
    public string? CollectionName { get; set; }

    [JsonPropertyName("score")]
    public double? Score { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("document_type")]
    public string? DocumentType { get; set; }

    [JsonPropertyName("source")]
    public string? SourceName { get; set; }

    [JsonPropertyName("stage")]
    public string? Stage { get; set; }

    [JsonPropertyName("metadata")]
    public CitationMetadata? Metadata { get; set; }
}

public sealed class CitationMetadata
{
    [JsonPropertyName("page_number")]
    public int? PageNumber { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("source_location")]
    public string? SourceLocation { get; set; }
}

// ── Collections ───────────────────────────────────────────────────────────────

public sealed class CollectionDetailInfo
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("owner")]
    public string? Owner { get; set; }

    [JsonPropertyName("business_domain")]
    public string? BusinessDomain { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

public sealed class CollectionInfo
{
    [JsonPropertyName("collection_name")]
    public string CollectionName { get; set; } = "";

    [JsonPropertyName("num_entities")]
    public int NumEntities { get; set; }

    [JsonPropertyName("metadata_schema")]
    public List<MetadataFieldDef> MetadataSchema { get; set; } = [];

    [JsonPropertyName("collection_info")]
    public CollectionDetailInfo? CollectionDetail { get; set; }

    public string? Description => CollectionDetail?.Description;
    public List<string>? Tags => CollectionDetail?.Tags;
    public string? Owner => CollectionDetail?.Owner;
    public string? BusinessDomain => CollectionDetail?.BusinessDomain;
    public string? Status => CollectionDetail?.Status;
}

public sealed class MetadataFieldDef
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("user_defined")]
    public bool UserDefined { get; set; } = true;

    [JsonPropertyName("support_dynamic_filtering")]
    public bool SupportDynamicFiltering { get; set; } = true;

    [JsonPropertyName("array_type")]
    public string? ArrayType { get; set; }

    [JsonPropertyName("max_length")]
    public int? MaxLength { get; set; }

    public MetadataFieldDef Normalized()
    {
        var type = NormalizeType(Type);
        return new MetadataFieldDef
        {
            Name = Name.Trim(),
            Type = type,
            Description = Description,
            Required = Required,
            UserDefined = UserDefined,
            SupportDynamicFiltering = SupportDynamicFiltering,
            ArrayType = type == "array" ? NormalizeType(ArrayType) : null,
            MaxLength = MaxLength is > 0 ? MaxLength : null
        };
    }

    public static string NormalizeType(string? type)
    {
        return (type ?? "string").Trim().ToLowerInvariant() switch
        {
            "str" => "string",
            "int" => "integer",
            "bool" => "boolean",
            "double" => "float",
            "" => "string",
            var value => value
        };
    }
}

public sealed class CollectionListResponse
{
    [JsonPropertyName("collections")]
    public List<CollectionInfo> Collections { get; set; } = [];

    [JsonPropertyName("total_collections")]
    public int TotalCollections { get; set; }
}

public sealed class CreateCollectionRequest
{
    [JsonPropertyName("collection_name")]
    public string CollectionName { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("owner")]
    public string Owner { get; set; } = "";

    [JsonPropertyName("business_domain")]
    public string BusinessDomain { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Active";

    [JsonPropertyName("metadata_schema")]
    public List<MetadataFieldDef> MetadataSchema { get; set; } = [];

    [JsonPropertyName("created_by")]
    public string CreatedBy { get; set; } = "";
}

// ── Documents ─────────────────────────────────────────────────────────────────

public sealed class DocumentInfo
{
    [JsonPropertyName("document_id")]
    public string DocumentId { get; set; } = "";

    [JsonPropertyName("document_name")]
    public string DocumentName { get; set; } = "";

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object?>? Metadata { get; set; }

    // Catalog metadata (description, tags) stored separately from schema metadata
    [JsonPropertyName("document_info")]
    public Dictionary<string, object?>? DocumentInfoData { get; set; }
}

public sealed class DocumentListResponse
{
    [JsonPropertyName("documents")]
    public List<DocumentInfo> Documents { get; set; } = [];

    [JsonPropertyName("total_documents")]
    public int TotalDocuments { get; set; }
}

// ── Ingestion Tasks ───────────────────────────────────────────────────────────

public sealed class IngestionTaskResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("task_id")]
    public string? TaskId { get; set; }
}

public sealed class IngestionTaskStatus
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "PENDING";

    [JsonPropertyName("result")]
    public UploadResult? Result { get; set; }
}

public sealed class FailedDocument
{
    [JsonPropertyName("document_name")]
    public string DocumentName { get; set; } = "";

    [JsonPropertyName("error_message")]
    public string ErrorMessage { get; set; } = "";
}

public sealed class UploadResult
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("total_documents")]
    public int TotalDocuments { get; set; }

    [JsonPropertyName("documents_completed")]
    public int DocumentsCompleted { get; set; }

    [JsonPropertyName("batches_completed")]
    public int BatchesCompleted { get; set; }

    [JsonPropertyName("documents")]
    public List<DocumentInfo> Documents { get; set; } = [];

    [JsonPropertyName("failed_documents")]
    public List<FailedDocument> FailedDocuments { get; set; } = [];

    [JsonPropertyName("validation_errors")]
    public List<JsonElement> ValidationErrors { get; set; } = [];
}

// ── Health ────────────────────────────────────────────────────────────────────

public sealed class HealthResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("databases")]
    public List<ServiceStatus>? Databases { get; set; }

    [JsonPropertyName("object_storage")]
    public List<ServiceStatus>? ObjectStorage { get; set; }

    [JsonPropertyName("nim")]
    public List<ServiceStatus>? Nim { get; set; }
}

public sealed class ServiceStatus
{
    // "service" not "name" — matches server field name
    [JsonPropertyName("service")]
    public string? Service { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    // Integer enum: 0=Healthy, 1=Unhealthy, 2=Skipped, 3=Timeout, 4=Error, 5=Unknown
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("latency_ms")]
    public double? LatencyMs { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

// ── Configuration ─────────────────────────────────────────────────────────────

public sealed class ConfigurationResponse
{
    [JsonPropertyName("rag_configuration")]
    public RagConfigDefaults? RagConfiguration { get; set; }

    [JsonPropertyName("feature_toggles")]
    public FeatureToggles? FeatureToggles { get; set; }

    [JsonPropertyName("models")]
    public ModelsConfig? Models { get; set; }

    [JsonPropertyName("endpoints")]
    public EndpointsConfig? Endpoints { get; set; }

    [JsonPropertyName("providers")]
    public ProvidersConfig? Providers { get; set; }
}

public sealed class RagConfigDefaults
{
    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public double TopP { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("vdb_top_k")]
    public int VdbTopK { get; set; }

    [JsonPropertyName("reranker_top_k")]
    public int RerankerTopK { get; set; }

    [JsonPropertyName("confidence_threshold")]
    public double ConfidenceScoreThreshold { get; set; }
}

public sealed class FeatureToggles
{
    [JsonPropertyName("enable_reranker")]
    public bool EnableReranker { get; set; }

    [JsonPropertyName("enable_citations")]
    public bool EnableCitations { get; set; }

    [JsonPropertyName("enable_query_rewriting")]
    public bool EnableQueryRewriting { get; set; }

    [JsonPropertyName("enable_query_decomposition")]
    public bool EnableQueryDecomposition { get; set; }

    [JsonPropertyName("enable_guardrails")]
    public bool EnableGuardrails { get; set; }

    [JsonPropertyName("enable_vlm_inference")]
    public bool EnableVlmInference { get; set; }

    [JsonPropertyName("enable_filter_generator")]
    public bool EnableFilterGenerator { get; set; }

    [JsonPropertyName("enable_agentic_rag")]
    public bool EnableAgenticRag { get; set; }
}

public sealed class ModelsConfig
{
    [JsonPropertyName("llm_model")]
    public string? LlmModelName { get; set; }

    [JsonPropertyName("embedding_model")]
    public string? EmbeddingModelName { get; set; }

    [JsonPropertyName("reranker_model")]
    public string? RankingModelName { get; set; }

    [JsonPropertyName("vlm_model")]
    public string? VlmModelName { get; set; }

    [JsonPropertyName("query_rewriter_model")]
    public string? QueryRewriterModelName { get; set; }

    [JsonPropertyName("filter_expression_generator_model")]
    public string? FilterExpressionGeneratorModelName { get; set; }

    [JsonPropertyName("reflection_model")]
    public string? ReflectionModelName { get; set; }
}

public sealed class EndpointsConfig
{
    [JsonPropertyName("llm_endpoint")]
    public string? LlmServerUrl { get; set; }

    [JsonPropertyName("embedding_endpoint")]
    public string? EmbeddingServerUrl { get; set; }

    [JsonPropertyName("reranker_endpoint")]
    public string? RankingServerUrl { get; set; }

    [JsonPropertyName("vlm_endpoint")]
    public string? VlmServerUrl { get; set; }

    [JsonPropertyName("vdb_endpoint")]
    public string? VdbEndpoint { get; set; }

    [JsonPropertyName("query_rewriter_endpoint")]
    public string? QueryRewriterEndpoint { get; set; }

    [JsonPropertyName("filter_expression_generator_endpoint")]
    public string? FilterExpressionGeneratorEndpoint { get; set; }

    [JsonPropertyName("reflection_endpoint")]
    public string? ReflectionEndpoint { get; set; }
}

public sealed class ProvidersConfig
{
    [JsonPropertyName("llm_provider")]
    public string? LlmProvider { get; set; }

    [JsonPropertyName("embedding_provider")]
    public string? EmbeddingProvider { get; set; }

    [JsonPropertyName("vlm_provider")]
    public string? VlmProvider { get; set; }

    [JsonPropertyName("vector_store_provider")]
    public string? VectorStoreProvider { get; set; }
}

// ── Filters ───────────────────────────────────────────────────────────────────

public sealed class FilterCondition
{
    public string Field { get; set; } = "";
    public string Operator { get; set; } = "==";
    public string Value { get; set; } = "";
    public string LogicalOp { get; set; } = "AND";
}

// ── Toasts ────────────────────────────────────────────────────────────────────

public sealed record ToastMessage(string Id, string Message, ToastSeverity Severity);

public enum ToastSeverity { Info, Success, Warning, Error }

// ── New Collection Form ───────────────────────────────────────────────────────

public sealed class NewCollectionForm
{
    public string CollectionName { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string Owner { get; set; } = "";
    public string BusinessDomain { get; set; } = "";
    public string Status { get; set; } = "Active";
    public bool GenerateSummary { get; set; } = true;
    public UploadExtractionOptions ExtractionOptions { get; set; } = new();
    public UploadSplitOptions SplitOptions { get; set; } = new();
    public List<MetadataFieldDef> Schema { get; set; } = [];
    public List<UploadFile> Files { get; set; } = [];
}

public sealed class UploadExtractionOptions
{
    [JsonPropertyName("extract_text")]
    public bool ExtractText { get; set; } = true;

    [JsonPropertyName("extract_tables")]
    public bool ExtractTables { get; set; } = true;

    [JsonPropertyName("extract_charts")]
    public bool ExtractCharts { get; set; } = true;

    [JsonPropertyName("extract_images")]
    public bool ExtractImages { get; set; }

    [JsonPropertyName("extract_method")]
    public string ExtractMethod { get; set; } = "pdfium";

    [JsonPropertyName("text_depth")]
    public string TextDepth { get; set; } = "page";
}

public sealed class UploadSplitOptions
{
    [JsonPropertyName("chunk_size")]
    public int ChunkSize { get; set; } = 1024;

    [JsonPropertyName("chunk_overlap")]
    public int ChunkOverlap { get; set; } = 150;
}

public sealed class UploadFile
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string ContentType { get; set; } = "";
    public Dictionary<string, string> Metadata { get; set; } = [];
    public byte[]? Data { get; set; }
}
