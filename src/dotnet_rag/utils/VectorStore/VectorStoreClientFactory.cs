using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Embedding;
using DotnetRag.Shared.Options;
using Microsoft.Extensions.Logging;

namespace DotnetRag.Shared.VectorStore;

public sealed class VectorStoreClientFactory(
    IHttpClientFactory httpClientFactory,
    IEmbeddingService embedder,
    RagServerConfiguration config,
    ILogger<ChromaDbVectorStore> chromaLogger,
    ILogger<MilvusVectorStore> milvusLogger,
    ILogger<OllamaEmbeddingService> ollamaEmbeddingLogger,
    ILogger<OpenAiEmbeddingService> openAiEmbeddingLogger) : IVectorStoreClientFactory
{
    public VectorStoreClient Create(
        string? endpoint = null,
        string? bearerToken = null,
        string? embeddingEndpoint = null,
        string? embeddingModel = null)
    {
        var activeEmbedder = CreateEmbeddingService(embeddingEndpoint, embeddingModel);
        var opts = new VectorStoreOptions
        {
            Provider = config.VectorStoreName,
            Endpoint = string.IsNullOrWhiteSpace(endpoint)
                ? config.VectorStoreUrl
                : endpoint.Trim(),
            CollectionName = config.CollectionName
        };

        var provider = config.VectorStoreName.Trim().ToLowerInvariant();
        if (provider == "milvus")
        {
            var token = string.IsNullOrWhiteSpace(bearerToken)
                ? config.MilvusToken
                : bearerToken;
            var store = new MilvusVectorStore(
                httpClientFactory,
                activeEmbedder,
                opts,
                config.EmbeddingDim,
                string.IsNullOrWhiteSpace(token) ? null : token,
                milvusLogger);
            return new VectorStoreClient(store, store, store);
        }

        var chroma = new ChromaDbVectorStore(
            httpClientFactory,
            activeEmbedder,
            opts,
            chromaLogger,
            string.IsNullOrWhiteSpace(bearerToken) ? null : bearerToken);
        return new VectorStoreClient(chroma, chroma, chroma);
    }

    private IEmbeddingService CreateEmbeddingService(
        string? embeddingEndpoint,
        string? embeddingModel)
    {
        if (string.IsNullOrWhiteSpace(embeddingEndpoint)
            && string.IsNullOrWhiteSpace(embeddingModel))
        {
            return embedder;
        }

        var endpoint = string.IsNullOrWhiteSpace(embeddingEndpoint)
            ? config.EmbeddingEndpoint
            : embeddingEndpoint.Trim();
        var model = string.IsNullOrWhiteSpace(embeddingModel)
            ? config.EmbeddingModel
            : embeddingModel.Trim();
        var provider = ResolveEmbeddingProvider(endpoint, config.EmbeddingProvider);

        if (provider == "ollama")
        {
            return new OllamaEmbeddingService(
                httpClientFactory,
                model,
                NormalizeOllamaBase(endpoint),
                ollamaEmbeddingLogger);
        }

        return new OpenAiEmbeddingService(
            httpClientFactory,
            model,
            endpoint,
            Environment.GetEnvironmentVariable("NVIDIA_API_KEY"),
            openAiEmbeddingLogger);
    }

    private static string ResolveEmbeddingProvider(string endpoint, string configuredProvider)
    {
        var normalizedEndpoint = endpoint.Trim().ToLowerInvariant();
        if (normalizedEndpoint.Contains("11434", StringComparison.Ordinal)
            || normalizedEndpoint.Contains("/api/embed", StringComparison.Ordinal)
            || normalizedEndpoint.Contains("/api/embeddings", StringComparison.Ordinal))
        {
            return "ollama";
        }

        if (normalizedEndpoint.Contains("/v1", StringComparison.Ordinal)
            || normalizedEndpoint.Contains("/embeddings", StringComparison.Ordinal))
        {
            return "openai";
        }

        var normalizedProvider = configuredProvider.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalizedProvider)
            ? "openai"
            : normalizedProvider;
    }

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
            "/v1/embeddings",
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

        return normalized.Contains(':')
            ? normalized.TrimEnd('/')
            : "http://localhost:11434";
    }
}
