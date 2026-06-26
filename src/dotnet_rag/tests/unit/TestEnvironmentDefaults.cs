using System.Runtime.CompilerServices;

namespace DotnetRag.Tests.Unit;

internal static class TestEnvironmentDefaults
{
    [ModuleInitializer]
    public static void Initialize()
    {
        SetIfMissing("APP_LLM_PROVIDER", "ollama");
        SetIfMissing("APP_LLM_SERVERURL", "http://localhost:11434");
        SetIfMissing("APP_LLM_MODELNAME", "nemotron-mini:latest");
        SetIfMissing("LLM_TEMPERATURE", "0.0");
        SetIfMissing("LLM_TOP_P", "1.0");
        SetIfMissing("LLM_MAX_TOKENS", "4096");

        SetIfMissing("APP_EMBEDDINGS_PROVIDER", "ollama");
        SetIfMissing("APP_EMBEDDINGS_SERVERURL", "http://localhost:11434");
        SetIfMissing("APP_EMBEDDINGS_MODELNAME", "snowflake-arctic-embed:22m");
        SetIfMissing("APP_EMBEDDINGS_DIM", "384");

        SetIfMissing("APP_RERANKER_SERVICE_URL", "http://localhost:8083");
        SetIfMissing("APP_RANKING_PROVIDER", "ollama");
        SetIfMissing("APP_RANKING_SERVERURL", "http://localhost:11434");
        SetIfMissing("APP_RANKING_MODELNAME", "snowflake-arctic-embed:22m");
        SetIfMissing("APP_RANKING_FALLBACK_PROVIDER", "lexical");
        SetIfMissing("APP_RANKING_TIMEOUT_SECONDS", "20");

        SetIfMissing("APP_NVINGEST_CHUNKSIZE", "512");
        SetIfMissing("APP_NVINGEST_CHUNKOVERLAP", "150");
        SetIfMissing("SUMMARIZER_MAX_CHUNK_LENGTH", "4096");
        SetIfMissing("SUMMARIZER_CHUNK_OVERLAP", "200");
        SetIfMissing("SUMMARIZER_TEMPERATURE", "0.1");
        SetIfMissing("SUMMARIZER_MAX_PARALLELIZATION", "2");
    }

    private static void SetIfMissing(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
