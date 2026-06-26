using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Options;
using Microsoft.Extensions.Logging;

namespace DotnetRag.Shared.VectorStore;

public sealed class VectorStoreClientFactory(
    IHttpClientFactory httpClientFactory,
    IEmbeddingService embedder,
    RagServerConfiguration config,
    ILogger<ChromaDbVectorStore> chromaLogger,
    ILogger<MilvusVectorStore> milvusLogger) : IVectorStoreClientFactory
{
    public VectorStoreClient Create(
        string? endpoint = null,
        string? bearerToken = null)
    {
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
                embedder,
                opts,
                config.EmbeddingDim,
                string.IsNullOrWhiteSpace(token) ? null : token,
                milvusLogger);
            return new VectorStoreClient(store, store);
        }

        var chroma = new ChromaDbVectorStore(
            httpClientFactory,
            embedder,
            opts,
            chromaLogger,
            string.IsNullOrWhiteSpace(bearerToken) ? null : bearerToken);
        return new VectorStoreClient(chroma, chroma);
    }
}
