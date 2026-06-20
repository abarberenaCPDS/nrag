namespace DotnetRag.Shared.Configuration;

public sealed class RagServerConfiguration
{
    public string Prompt_Config_File { get; init; } = GetEnv("PROMPT_CONFIG_FILE", @Path.Combine(AppContext.BaseDirectory, "prompt.yaml"));
    public string VectorStoreName { get; init; } = GetEnv("APP_VECTORSTORE_NAME", "chroma"); // ORIG_VECTORSTORE_NAME: elasticsearch
    public string VectorStoreUrl { get; init; } = GetEnv("APP_VECTORSTORE_URL", "http://localhost:8000"); // ORIG_VECTORSTORE_URL: http://localhost:9200 (elasticsearch) / milvus:19530
    public string CollectionName { get; init; } = GetEnv("COLLECTION_NAME", "multimodal_data");

    // ORIG_LLM_MODELNAME: nvidia/nemotron-3-super-120b-a12b
    public string LlmModel { get; init; } = GetEnv("APP_LLM_MODELNAME", "qwen2.5:3b");
    // ORIG_LLM_SERVERURL: nim-llm:8000
    public string LlmEndpoint { get; init; } = GetEnv("APP_LLM_SERVERURL", "http://localhost:11434");
    public string LlmProvider { get; init; } = GetEnv("APP_LLM_PROVIDER", "ollama"); // ORIG_LLM_PROVIDER: openai (NIM)
    // ORIG_EMBED_MODEL: nvidia/llama-nemotron-embed-vl-1b-v2
    public string EmbeddingModel { get; init; } = GetEnv("APP_EMBEDDINGS_MODELNAME", "snowflake-arctic-embed:22m");
    //public string EmbeddingModel { get; init; } = GetEnv("APP_EMBEDDINGS_MODELNAME", "nomic-embed-text");
    // ORIG_EMBED_ENDPOINT: nemotron-vlm-embedding-ms:8000/v1
    public string EmbeddingEndpoint { get; init; } = GetEnv("APP_EMBEDDINGS_SERVERURL", "http://localhost:11434");
    // ORIG_RERANKER_MODEL: nvidia/llama-nemotron-rerank-1b-v2
    public string RerankerModel { get; init; } = GetEnv("APP_RANKING_MODELNAME", "");
    // ORIG_RERANKER_ENDPOINT: nemotron-ranking-ms:8000
    public string RerankerEndpoint { get; init; } = GetEnv("APP_RANKING_SERVERURL", "");
    // ORIG_VLM_MODEL: nvidia/nemotron-3-nano-omni-30b-a3b-reasoning
    public string VlmModel { get; init; } = GetEnv("APP_VLM_MODELNAME", "");
    // ORIG_VLM_ENDPOINT: http://vlm-ms:8000/v1
    public string VlmEndpoint { get; init; } = GetEnv("APP_VLM_SERVERURL", "");

    public double Temperature { get; init; } = GetEnvDouble("LLM_TEMPERATURE", 0.0);
    public double TopP { get; init; } = GetEnvDouble("LLM_TOP_P", 1.0);
    public int MaxTokens { get; init; } = GetEnvInt("LLM_MAX_TOKENS", 16256);
    public int VdbTopK { get; init; } = GetEnvInt("VECTOR_DB_TOPK", 100);
    public int RerankerTopK { get; init; } = GetEnvInt("APP_RETRIEVER_TOPK", 10);
    public double ConfidenceThreshold { get; init; } = GetEnvDouble("APP_RETRIEVER_SCORETHRESHOLD", 0.25);

    public bool EnableReranker { get; init; } = GetEnvBool("ENABLE_RERANKER", true);
    public bool EnableCitations { get; init; } = GetEnvBool("ENABLE_CITATIONS", true);
    public bool EnableGuardrails { get; init; } = GetEnvBool("ENABLE_GUARDRAILS", false);
    public bool EnableQueryRewriting { get; init; } = GetEnvBool("ENABLE_QUERYREWRITER", false);
    public bool EnableVlmInference { get; init; } = GetEnvBool("ENABLE_VLM_INFERENCE", false);
    public bool EnableFilterGenerator { get; init; } = GetEnvBool("ENABLE_FILTER_GENERATOR", false);
    public bool EnableAgenticRag { get; init; } = GetEnvBool("ENABLE_AGENTIC_RAG", false);

    public bool TracingEnabled { get; init; } = GetEnvBool("APP_TRACING_ENABLED", false);
    public string? OtlpHttpEndpoint { get; init; } = GetEnv("APP_TRACING_OTLPHTTPENDPOINT", "http://otel-collector:4318/v1/traces");
    public string? OtlpGrpcEndpoint { get; init; } = GetEnv("APP_TRACING_OTLPGRPCENDPOINT", "grpc://otel-collector:4317");

    public int ChunkSize { get; init; } = GetEnvInt("APP_NVINGEST_CHUNKSIZE", 512);
    public int ChunkOverlap { get; init; } = GetEnvInt("APP_NVINGEST_CHUNKOVERLAP", 150);

    // ORIG: nvidia_rag/utils/configuration.py::SummarizerConfig
    // Defaults to the main LLM model/endpoint when not explicitly set — easy to swap for a smaller nano model.
    public string SummarizerModel { get; init; } = GetEnv("SUMMARIZER_MODEL", "");
    public string SummarizerEndpoint { get; init; } = GetEnv("SUMMARIZER_SERVERURL", "");
    public int SummarizerMaxChunkLength { get; init; } = GetEnvInt("SUMMARIZER_MAX_CHUNK_LENGTH", 4096);
    public int SummarizerChunkOverlap { get; init; } = GetEnvInt("SUMMARIZER_CHUNK_OVERLAP", 200);
    public double SummarizerTemperature { get; init; } = GetEnvDouble("SUMMARIZER_TEMPERATURE", 0.1);
    public int SummarizerMaxParallelization { get; init; } = GetEnvInt("SUMMARIZER_MAX_PARALLELIZATION", 2);

    public bool FilterThinkTokens { get; init; } = GetEnvBool("FILTER_THINK_TOKENS", true);
    public int ConversationHistory { get; init; } = GetEnvInt("CONVERSATION_HISTORY", 0);
    public bool MultiTurnRetrieverSimple { get; init; } = GetEnvBool("MULTITURN_RETRIEVER_SIMPLE", false);
    public bool EnableSourceMetadata { get; init; } = GetEnvBool("ENABLE_SOURCE_METADATA", true);

    public string LogLevel { get; init; } = GetEnv("LOGLEVEL", "INFO");

    public static RagServerConfiguration FromEnvironment() => new();

    private static string GetEnv(string name, string defaultValue) =>
        Environment.GetEnvironmentVariable(name)?.Trim().Trim('"').Trim('\'') ?? defaultValue;

    private static int GetEnvInt(string name, int defaultValue) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : defaultValue;

    private static double GetEnvDouble(string name, double defaultValue) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : defaultValue;

    private static bool GetEnvBool(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null)
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }
}
