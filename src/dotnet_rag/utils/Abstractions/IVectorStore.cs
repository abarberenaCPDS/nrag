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

    // Overload that accepts a Milvus-style boolean filter expression.
    // Default implementation ignores the filter; Milvus implementation applies it.
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collectionName,
        string query,
        int topK,
        string? filterExpr,
        CancellationToken cancellationToken = default)
        => SearchAsync(collectionName, query, topK, cancellationToken);

    // Returns a human-readable schema description for use by the filter expression generator.
    // Default returns an empty string (no schema info available).
    Task<string> GetSchemaDescriptionAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);
}
