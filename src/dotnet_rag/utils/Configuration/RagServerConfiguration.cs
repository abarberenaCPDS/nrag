namespace DotnetRag.Shared.Configuration;

public sealed class RagServerConfiguration
{
    public string Prompt_Config_File { get; init; } = GetEnv("PROMPT_CONFIG_FILE", @Path.Combine(AppContext.BaseDirectory, "prompt.yaml"));
    public string VectorStoreName { get; init; } = GetEnv("APP_VECTORSTORE_NAME", "chroma"); // ORIG_VECTORSTORE_NAME: elasticsearch
    public string VectorStoreUrl { get; init; } = GetEnv("APP_VECTORSTORE_URL", "http://localhost:8000"); // ORIG_VECTORSTORE_URL: http://localhost:9200 (elasticsearch) / milvus:19530
    public string CollectionName { get; init; } = GetEnv("COLLECTION_NAME", "multimodal_data");
    public string ObjectStoreRoot { get; init; } = GetEnv("APP_OBJECT_STORE_ROOT", "");

    // ORIG_LLM_MODELNAME: nvidia/nemotron-3-super-120b-a12b
    public string LlmModel { get; init; } = GetRequiredEnv("APP_LLM_MODELNAME");
    // ORIG_LLM_SERVERURL: nim-llm:8000
    public string LlmEndpoint { get; init; } = GetRequiredEnv("APP_LLM_SERVERURL");
    public string LlmProvider { get; init; } = GetRequiredEnv("APP_LLM_PROVIDER"); // ORIG_LLM_PROVIDER: openai (NIM)
    // ORIG_EMBED_MODEL: nvidia/llama-nemotron-embed-vl-1b-v2
    public string EmbeddingModel { get; init; } = GetRequiredEnv("APP_EMBEDDINGS_MODELNAME");
    // ORIG_EMBED_ENDPOINT: nemotron-vlm-embedding-ms:8000/v1
    public string EmbeddingEndpoint { get; init; } = GetRequiredEnv("APP_EMBEDDINGS_SERVERURL");
    public string EmbeddingProvider { get; init; } = GetRequiredEnv("APP_EMBEDDINGS_PROVIDER");
    // ORIG_RERANKER_MODEL: nvidia/llama-nemotron-rerank-1b-v2
    public string RerankerModel { get; init; } = GetEnv("APP_RANKING_MODELNAME", "");
    // ORIG_RERANKER_ENDPOINT: nemotron-ranking-ms:8000
    public string RerankerEndpoint { get; init; } = GetEnv("APP_RANKING_SERVERURL", "");
    public string RerankerServiceUrl { get; init; } = GetEnv("APP_RERANKER_SERVICE_URL", "http://localhost:8083");
    public int RerankerServiceTimeoutSeconds { get; init; } = GetEnvInt("APP_RERANKER_SERVICE_TIMEOUT_SECONDS", 10);
    public int LlmHttpTimeoutSeconds { get; init; } = GetEnvInt("APP_LLM_HTTP_TIMEOUT_SECONDS", 600);
    // ORIG_VLM_MODEL: nvidia/nemotron-3-nano-omni-30b-a3b-reasoning
    public string VlmModel { get; init; } = GetEnv("APP_VLM_MODELNAME", "");
    // ORIG_VLM_ENDPOINT: http://vlm-ms:8000/v1
    public string VlmEndpoint { get; init; } = GetEnv("APP_VLM_SERVERURL", "");
    public string VlmProvider { get; init; } = GetEnv("APP_VLM_PROVIDER", "");
    public string QueryRewriterModel { get; init; } = GetEnv("APP_QUERYREWRITER_MODELNAME", "");
    public string QueryRewriterEndpoint { get; init; } = GetEnv("APP_QUERYREWRITER_SERVERURL", "");
    public string QueryRewriterApiKey { get; init; } = GetEnv("APP_QUERYREWRITER_APIKEY", "");
    public string FilterExpressionGeneratorModel { get; init; } = GetEnv("APP_FILTEREXPRESSIONGENERATOR_MODELNAME", "");
    public string FilterExpressionGeneratorEndpoint { get; init; } = GetEnv("APP_FILTEREXPRESSIONGENERATOR_SERVERURL", "");
    public string FilterExpressionGeneratorApiKey { get; init; } = GetEnv("APP_FILTEREXPRESSIONGENERATOR_APIKEY", "");
    public string ReflectionModel { get; init; } = GetEnv("REFLECTION_LLM", "");
    public string ReflectionEndpoint { get; init; } = GetEnv("REFLECTION_LLM_SERVERURL", "");
    public string ReflectionApiKey { get; init; } = GetEnv("REFLECTION_LLM_APIKEY", "");

    public double Temperature { get; init; } = GetRequiredEnvDouble("LLM_TEMPERATURE");
    public double TopP { get; init; } = GetRequiredEnvDouble("LLM_TOP_P");
    public int MaxTokens { get; init; } = GetRequiredEnvInt("LLM_MAX_TOKENS");
    public double FilterExpressionGeneratorTemperature { get; init; } = GetEnvDouble("APP_FILTEREXPRESSIONGENERATOR_TEMPERATURE", 0.0);
    public double FilterExpressionGeneratorTopP { get; init; } = GetEnvDouble("APP_FILTEREXPRESSIONGENERATOR_TOPP", 1.0);
    public int FilterExpressionGeneratorMaxTokens { get; init; } = GetEnvInt("APP_FILTEREXPRESSIONGENERATOR_MAXTOKENS", 32768);
    public int VdbTopK { get; init; } = GetEnvInt("VECTOR_DB_TOPK", 100);
    public int RerankerTopK { get; init; } = GetEnvInt("APP_RETRIEVER_TOPK", 10);
    public double ConfidenceThreshold { get; init; } = GetEnvDouble("APP_RETRIEVER_SCORETHRESHOLD", 0.25);

    public bool EnableReranker { get; init; } = GetEnvBool("ENABLE_RERANKER", true);
    public bool EnableCitations { get; init; } = GetEnvBool("ENABLE_CITATIONS", true);
    public bool EnableGuardrails { get; init; } = GetEnvBool("ENABLE_GUARDRAILS", false);
    public bool EnableQueryRewriting { get; init; } = GetEnvBool("ENABLE_QUERYREWRITER", false);
    public bool EnableQueryDecomposition { get; init; } = GetEnvBool("ENABLE_QUERY_DECOMPOSITION", false);
    public bool EnableVlmInference { get; init; } = GetEnvBool("ENABLE_VLM_INFERENCE", false);
    public bool EnableFilterGenerator { get; init; } = GetEnvBool("ENABLE_FILTER_GENERATOR", false);
    public bool EnableAgenticRag { get; init; } = GetEnvBool("ENABLE_AGENTIC_RAG", false);
    public int AgenticPlannerMaxTasks { get; init; } = GetEnvInt("AGENTIC_PLANNER_MAX_TASKS", 5);
    public int AgenticPlannerMaxScopeRounds { get; init; } = GetEnvInt("AGENTIC_PLANNER_MAX_SCOPE_ROUNDS", 2);
    public int AgenticPlannerMaxAttempts { get; init; } = GetEnvInt("AGENTIC_PLANNER_MAX_ATTEMPTS", 3);
    public string AgenticPlannerModel { get; init; } = GetEnv("AGENTIC_PLANNER_LLM_MODEL", "");
    public double AgenticPlannerTemperature { get; init; } = GetEnvDouble("AGENTIC_PLANNER_LLM_TEMPERATURE", 0.0);
    public double AgenticPlannerTopP { get; init; } = GetEnvDouble("AGENTIC_PLANNER_LLM_TOP_P", 0.1);
    public int AgenticPlannerMaxTokens { get; init; } = GetEnvInt("AGENTIC_PLANNER_LLM_MAX_TOKENS", 1024);
    public int AgenticTaskScopeMaxRetries { get; init; } = GetEnvInt("AGENTIC_TASK_SCOPE_MAX_RETRIES", 1);
    public int AgenticTaskAnswerMaxRetries { get; init; } = GetEnvInt("AGENTIC_TASK_ANSWER_MAX_RETRIES", 3);
    public string AgenticTaskModel { get; init; } = GetEnv("AGENTIC_TASK_LLM_MODEL", "");
    public string AgenticSeedGenerationModel { get; init; } = GetEnv("AGENTIC_SEED_GEN_LLM_MODEL", "");
    public string AgenticSynthesisModel { get; init; } = GetEnv("AGENTIC_SYNTHESIS_LLM_MODEL", "");
    public int AgenticVerificationMaxTasks { get; init; } = GetEnvInt("AGENTIC_VERIFICATION_MAX_TASKS", 3);

    public bool TracingEnabled { get; init; } = GetEnvBool("APP_TRACING_ENABLED", false);
    public string? OtlpHttpEndpoint { get; init; } = GetEnv("APP_TRACING_OTLPHTTPENDPOINT", "http://otel-collector:4318/v1/traces");
    public string? OtlpGrpcEndpoint { get; init; } = GetEnv("APP_TRACING_OTLPGRPCENDPOINT", "grpc://otel-collector:4317");

    public int ChunkSize { get; init; } = GetRequiredEnvInt("APP_NVINGEST_CHUNKSIZE");
    public int ChunkOverlap { get; init; } = GetRequiredEnvInt("APP_NVINGEST_CHUNKOVERLAP");

    // ORIG: nvidia_rag/utils/configuration.py::SummarizerConfig
    // Defaults to the main LLM model/endpoint when not explicitly set — easy to swap for a smaller nano model.
    public string SummarizerModel { get; init; } = GetEnv("SUMMARIZER_MODEL", "");
    public string SummarizerEndpoint { get; init; } = GetEnv("SUMMARIZER_SERVERURL", "");
    public string SummaryStatusStorePath { get; init; } = GetEnv("APP_SUMMARY_STATUS_STORE_PATH", "");
    public int SummarizerMaxChunkLength { get; init; } = GetRequiredEnvInt("SUMMARIZER_MAX_CHUNK_LENGTH");
    public int SummarizerChunkOverlap { get; init; } = GetRequiredEnvInt("SUMMARIZER_CHUNK_OVERLAP");
    public double SummarizerTemperature { get; init; } = GetRequiredEnvDouble("SUMMARIZER_TEMPERATURE");
    public int SummarizerMaxParallelization { get; init; } = GetRequiredEnvInt("SUMMARIZER_MAX_PARALLELIZATION");

    public bool EnableReflection { get; init; } = GetEnvBool("ENABLE_REFLECTION", false);
    public int ReflectionContextThreshold { get; init; } = GetEnvInt("CONTEXT_RELEVANCE_THRESHOLD", GetEnvInt("REFLECTION_CONTEXT_THRESHOLD", 2));
    public int ReflectionGroundednessThreshold { get; init; } = GetEnvInt("RESPONSE_GROUNDEDNESS_THRESHOLD", GetEnvInt("REFLECTION_GROUNDEDNESS_THRESHOLD", 2));
    public int ReflectionMaxLoops { get; init; } = GetEnvInt("MAX_REFLECTION_LOOP", GetEnvInt("REFLECTION_MAX_LOOPS", 2));
    public int QueryDecompositionRecursionDepth { get; init; } = GetEnvInt("MAX_RECURSION_DEPTH", 3);

    public bool VlmToLlmFallback { get; init; } = GetEnvBool("VLM_TO_LLM_FALLBACK", true);
    public int VlmMaxTotalImages { get; init; } = GetEnvInt("VLM_MAX_TOTAL_IMAGES", 10);

    // Milvus-specific
    public string MilvusToken { get; init; } = GetEnv("MILVUS_TOKEN", "");
    public int EmbeddingDim { get; init; } = GetRequiredEnvInt("APP_EMBEDDINGS_DIM");

    public bool FilterThinkTokens { get; init; } = GetEnvBool("FILTER_THINK_TOKENS", true);
    public int ConversationHistory { get; init; } = GetEnvInt("CONVERSATION_HISTORY", 0);
    public bool MultiTurnRetrieverSimple { get; init; } = GetEnvBool("MULTITURN_RETRIEVER_SIMPLE", false);
    public bool EnableSourceMetadata { get; init; } = GetEnvBool("ENABLE_SOURCE_METADATA", true);

    public string LogLevel { get; init; } = GetEnv("LOGLEVEL", "INFO");
    public string QueryRewriterModelOrDefault => string.IsNullOrWhiteSpace(QueryRewriterModel) ? LlmModel : QueryRewriterModel;
    public string QueryRewriterEndpointOrDefault => string.IsNullOrWhiteSpace(QueryRewriterEndpoint) ? LlmEndpoint : QueryRewriterEndpoint;
    public string QueryRewriterApiKeyOrDefault => string.IsNullOrWhiteSpace(QueryRewriterApiKey) ? GetEnv("NVIDIA_API_KEY", "") : QueryRewriterApiKey;
    public string FilterExpressionGeneratorModelOrDefault => string.IsNullOrWhiteSpace(FilterExpressionGeneratorModel) ? LlmModel : FilterExpressionGeneratorModel;
    public string FilterExpressionGeneratorEndpointOrDefault => string.IsNullOrWhiteSpace(FilterExpressionGeneratorEndpoint) ? LlmEndpoint : FilterExpressionGeneratorEndpoint;
    public string FilterExpressionGeneratorApiKeyOrDefault => string.IsNullOrWhiteSpace(FilterExpressionGeneratorApiKey) ? GetEnv("NVIDIA_API_KEY", "") : FilterExpressionGeneratorApiKey;
    public string ReflectionModelOrDefault => string.IsNullOrWhiteSpace(ReflectionModel) ? LlmModel : ReflectionModel;
    public string ReflectionEndpointOrDefault => string.IsNullOrWhiteSpace(ReflectionEndpoint) ? LlmEndpoint : ReflectionEndpoint;
    public string ReflectionApiKeyOrDefault => string.IsNullOrWhiteSpace(ReflectionApiKey) ? GetEnv("NVIDIA_API_KEY", "") : ReflectionApiKey;
    public string AgenticPlannerModelOrDefault => string.IsNullOrWhiteSpace(AgenticPlannerModel) ? LlmModel : AgenticPlannerModel;
    public string AgenticTaskModelOrDefault => string.IsNullOrWhiteSpace(AgenticTaskModel) ? AgenticPlannerModelOrDefault : AgenticTaskModel;
    public string AgenticSeedGenerationModelOrDefault => string.IsNullOrWhiteSpace(AgenticSeedGenerationModel) ? AgenticPlannerModelOrDefault : AgenticSeedGenerationModel;
    public string AgenticSynthesisModelOrDefault => string.IsNullOrWhiteSpace(AgenticSynthesisModel) ? AgenticPlannerModelOrDefault : AgenticSynthesisModel;

    public static RagServerConfiguration FromEnvironment()
    {
        var config = new RagServerConfiguration();
        config.Validate();
        return config;
    }

    public void Validate()
    {
        ValidateEmbeddingDimension();

        if (EnableReranker)
        {
            RequireConfigured(RerankerServiceUrl, "APP_RERANKER_SERVICE_URL");
            RequireConfigured(RerankerModel, "APP_RANKING_MODELNAME");
            RequireConfigured(RerankerEndpoint, "APP_RANKING_SERVERURL");
        }

        if (EnableVlmInference)
        {
            RequireConfigured(VlmProvider, "APP_VLM_PROVIDER");
            RequireConfigured(VlmModel, "APP_VLM_MODELNAME");
            RequireConfigured(VlmEndpoint, "APP_VLM_SERVERURL");
        }
    }

    private static string GetEnv(string name, string defaultValue) =>
        Environment.GetEnvironmentVariable(name)?.Trim().Trim('"').Trim('\'') ?? defaultValue;

    private static string GetRequiredEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim().Trim('"').Trim('\'');
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException($"{name} must be set in the environment.");
    }

    private static int GetEnvInt(string name, int defaultValue) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : defaultValue;

    private static int GetRequiredEnvInt(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (int.TryParse(raw, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"{name} must be set to a valid integer in the environment.");
    }

    private static double GetEnvDouble(string name, double defaultValue) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : defaultValue;

    private static double GetRequiredEnvDouble(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (double.TryParse(raw, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"{name} must be set to a valid number in the environment.");
    }

    private static bool GetEnvBool(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null)
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static void RequireConfigured(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} must be set when its feature/provider is enabled.");
        }
    }

    private void ValidateEmbeddingDimension()
    {
        var expectedDim = EmbeddingModel.Trim().ToLowerInvariant() switch
        {
            "snowflake-arctic-embed:22m" => 384,
            "nomic-embed-text" => 768,
            _ => (int?)null
        };

        if (expectedDim is not int expected || EmbeddingDim == expected)
        {
            return;
        }

        throw new InvalidOperationException(
            $"APP_EMBEDDINGS_DIM={EmbeddingDim} does not match APP_EMBEDDINGS_MODELNAME={EmbeddingModel}. " +
            $"Expected APP_EMBEDDINGS_DIM={expected} for this model. Existing Milvus collections must be recreated " +
            "after changing embedding dimensions.");
    }
}
