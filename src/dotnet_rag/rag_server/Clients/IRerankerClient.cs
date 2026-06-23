using DotnetRag.Shared.Abstractions;

namespace DotnetRag.Rag.Clients;

public interface IRerankerClient
{
    Task<IReadOnlyList<VectorSearchResult>> RerankAsync(
        string query,
        IReadOnlyList<VectorSearchResult> candidates,
        int topK,
        CancellationToken cancellationToken = default);
}
