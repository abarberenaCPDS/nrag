namespace DotnetRag.Shared.Abstractions;

public interface IVectorStore
{
    Task UpsertAsync(
        string collectionName,
        IReadOnlyList<VectorDocument> documents,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collectionName,
        string query,
        int topK,
        CancellationToken cancellationToken = default);
}
