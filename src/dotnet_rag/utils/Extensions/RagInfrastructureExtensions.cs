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

        // Embedding service — Ollama for local by default; OpenAI-compatible NIM for cloud.
        services.AddSingleton<IEmbeddingService>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var provider = config.EmbeddingProvider.Trim().ToLowerInvariant();
            if (provider is "openai" or "openai-compatible" or "openai_compatible")
            {
                var openAiLogger = sp.GetRequiredService<ILogger<OpenAiEmbeddingService>>();
                var apiKey = Environment.GetEnvironmentVariable("NVIDIA_API_KEY");
                return new OpenAiEmbeddingService(
                    factory,
                    config.EmbeddingModel,
                    config.EmbeddingEndpoint,
                    apiKey,
                    openAiLogger);
            }

            var ollamaLogger = sp.GetRequiredService<ILogger<OllamaEmbeddingService>>();
            var baseUrl = NormalizeOllamaBase(config.EmbeddingEndpoint);
            return new OllamaEmbeddingService(factory, config.EmbeddingModel, baseUrl, ollamaLogger);
        });

        // Vector store — ChromaDB or Milvus based on APP_VECTORSTORE_NAME
        var storeName = config.VectorStoreName.Trim().ToLowerInvariant();
        services.AddHttpClient("milvus");
        services.AddSingleton<ChromaDbVectorStore>();

        if (storeName == "milvus")
        {
            services.AddSingleton<MilvusVectorStore>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<MilvusVectorStore>>();
                var factory = sp.GetRequiredService<IHttpClientFactory>();
                var embedder = sp.GetRequiredService<IEmbeddingService>();
                return new MilvusVectorStore(
                    factory, embedder, vectorStoreOpts,
                    config.EmbeddingDim,
                    string.IsNullOrWhiteSpace(config.MilvusToken) ? null : config.MilvusToken,
                    logger);
            });
            services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<MilvusVectorStore>());
            services.AddSingleton<IVectorStoreManagement>(sp => sp.GetRequiredService<MilvusVectorStore>());
            services.AddSingleton<IVectorDocumentLookup>(sp => sp.GetRequiredService<MilvusVectorStore>());
        }
        else
        {
            services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<ChromaDbVectorStore>());
            services.AddSingleton<IVectorStoreManagement>(sp => sp.GetRequiredService<ChromaDbVectorStore>());
            services.AddSingleton<IVectorDocumentLookup>(sp => sp.GetRequiredService<ChromaDbVectorStore>());
        }
        services.AddSingleton<IVectorStoreClientFactory, VectorStoreClientFactory>();

        // Chat completion service — Ollama or OpenAI-compatible depending on APP_LLM_PROVIDER
        var llmProvider = config.LlmProvider.Trim().ToLowerInvariant();
        if (llmProvider == "ollama")
        {
            services.AddSingleton<IChatCompletionService>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<OllamaChatCompletionService>>();
                var factory = sp.GetRequiredService<IHttpClientFactory>();
                var baseUrl = NormalizeOllamaBase(config.LlmEndpoint);
                return new OllamaChatCompletionService(factory, config.LlmModel, baseUrl, logger);
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

        // VLM service — Ollama for local multimodal models, OpenAI-compatible for hosted NIM/OpenAI.
        if (!string.IsNullOrWhiteSpace(config.VlmEndpoint) && !string.IsNullOrWhiteSpace(config.VlmModel))
        {
            services.AddKeyedSingleton<IChatCompletionService>("vlm", (sp, _) =>
            {
                var factory = sp.GetRequiredService<IHttpClientFactory>();
                var provider = string.IsNullOrWhiteSpace(config.VlmProvider)
                    ? config.LlmProvider
                    : config.VlmProvider;

                if (provider.Trim().Equals("ollama", StringComparison.OrdinalIgnoreCase))
                {
                    var ollamaLogger = sp.GetRequiredService<ILogger<OllamaChatCompletionService>>();
                    var baseUrl = NormalizeOllamaBase(config.VlmEndpoint);
                    return new OllamaChatCompletionService(factory, config.VlmModel, baseUrl, ollamaLogger);
                }

                var vlmLogger = sp.GetRequiredService<ILogger<OpenAiChatCompletionService>>();
                var apiKey = Environment.GetEnvironmentVariable("NVIDIA_API_KEY");
                return new OpenAiChatCompletionService(
                    factory, config.VlmModel, config.VlmEndpoint, apiKey, vlmLogger);
            });
        }

        // Summarization stack
        services.AddSingleton(new SummarizationRateLimiter(config.SummarizerMaxParallelization));
        services.AddSingleton<SummaryProgressTracker>();
        services.AddSingleton<IObjectStore, DisabledSharedObjectStore>();
        services.AddSingleton<ISummarizationService, SummarizationService>();

        return services;
    }

    private sealed class DisabledSharedObjectStore : IObjectStore
    {
        public bool IsEnabled => false;
        public string BackendName => "disabled";

        public Task StoreJsonAsync(
            string collectionName,
            string objectName,
            IReadOnlyDictionary<string, object?> payload,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListAsync(
            string collectionName,
            string prefix,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task DeleteAsync(
            string collectionName,
            IReadOnlyList<string> objectNames,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    /// <summary>
    /// Strips native and OpenAI-compatible endpoint suffixes to get the bare Ollama base URL.
    /// </summary>
    private static string NormalizeOllamaBase(string url)
    {
        var normalized = url.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"http://{normalized}";
        }

        foreach (var suffix in new[]
        {
            "/v1/chat/completions",
            "/v1/embeddings",
            "/api/generate",
            "/api/chat",
            "/api/embed",
            "/api/embeddings",
            "/v1"
        })
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
