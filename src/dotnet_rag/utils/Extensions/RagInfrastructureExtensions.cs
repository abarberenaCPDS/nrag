using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Embedding;
using DotnetRag.Shared.LlmProviders;
using DotnetRag.Shared.Options;
using DotnetRag.Shared.Summarization;
using DotnetRag.Shared.VectorStore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotnetRag.Shared.Extensions;

/// <summary>
/// Extension methods for registering the RAG infrastructure stack
/// (LLM provider, embeddings, vector store) with the DI container.
/// </summary>
public static class RagInfrastructureExtensions
{
    /// <summary>
    /// Registers the full RAG stack based on <see cref="RagServerConfiguration"/>.
    /// Call this in both rag_server/Program.cs and ingestor_server/Program.cs.
    /// </summary>
    public static IServiceCollection AddRagInfrastructure(
        this IServiceCollection services,
        RagServerConfiguration config)
    {
        // Named HttpClients for each downstream service
        services.AddHttpClient("ollama");
        services.AddHttpClient("openai");
        services.AddHttpClient("chroma");

        // VectorStoreOptions bound from config
        var vectorStoreOpts = new VectorStoreOptions
        {
            Provider = config.VectorStoreName,
            // ORIG_VECTORSTORE_URL: elasticsearch / milvus endpoint
            Endpoint = config.VectorStoreUrl,
            CollectionName = config.CollectionName
        };
        services.AddSingleton(vectorStoreOpts);

        // Embedding service — always Ollama for local; OpenAI-compatible NIM for cloud
        services.AddSingleton<IEmbeddingService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<OllamaEmbeddingService>>();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            // ORIG_EMBED_MODEL: nvidia/llama-nemotron-embed-vl-1b-v2
            // Ollama local: nomic-embed-text (768 dims) — best general-purpose
            var model = config.EmbeddingModel is "nvidia/llama-nemotron-embed-vl-1b-v2"
                or "" or null
                ? "nomic-embed-text"
                : config.EmbeddingModel;

            // ORIG_EMBED_ENDPOINT: nemotron-vlm-embedding-ms:8000/v1
            var baseUrl = NormalizeOllamaBase(config.EmbeddingEndpoint);
            return new OllamaEmbeddingService(factory, model, baseUrl, logger);
        });

        // Vector store — ChromaDB REST
        services.AddSingleton<ChromaDbVectorStore>();
        services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<ChromaDbVectorStore>());

        // Chat completion service — Ollama or OpenAI-compatible depending on APP_LLM_PROVIDER
        var llmProvider = config.LlmProvider.Trim().ToLowerInvariant();
        if (llmProvider == "ollama")
        {
            services.AddSingleton<IChatCompletionService>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<OllamaChatCompletionService>>();
                var factory = sp.GetRequiredService<IHttpClientFactory>();
                // ORIG_LLM_MODEL: nvidia/nemotron-3-super-120b-a12b
                // Ollama nano model: qwen2.5:3b — 3B params, strong reasoning
                var model = config.LlmModel is "nvidia/nemotron-3-super-120b-a12b"
                    or "" or null
                    ? "qwen2.5:3b"
                    : config.LlmModel;
                var baseUrl = NormalizeOllamaBase(config.LlmEndpoint);
                return new OllamaChatCompletionService(factory, model, baseUrl, logger);
            });
        }
        else
        {
            services.AddSingleton<IChatCompletionService>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<OpenAiChatCompletionService>>();
                var factory = sp.GetRequiredService<IHttpClientFactory>();
                var apiKey = Environment.GetEnvironmentVariable("NVIDIA_API_KEY");
                return new OpenAiChatCompletionService(
                    factory,
                    config.LlmModel,
                    config.LlmEndpoint,
                    apiKey,
                    logger);
            });
        }

        // Summarization stack
        services.AddSingleton(new SummarizationRateLimiter(config.SummarizerMaxParallelization));
        services.AddSingleton<SummaryProgressTracker>();
        services.AddSingleton<ISummarizationService, SummarizationService>();

        return services;
    }

    /// <summary>
    /// Strips /v1, /api/embeddings, /api/chat suffixes to get the bare Ollama base URL.
    /// </summary>
    private static string NormalizeOllamaBase(string url)
    {
        var normalized = url.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"http://{normalized}";
        }

        foreach (var suffix in new[] { "/v1/chat/completions", "/v1/embeddings", "/api/chat", "/api/embeddings", "/v1" })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }

        // If nothing looks like a real Ollama URL, default to localhost
        if (!normalized.Contains(':'))
        {
            return "http://localhost:11434";
        }

        return normalized.TrimEnd('/');
    }
}
