namespace DotnetRag.Shared.Abstractions;

public sealed record VectorStoreCollectionDetails(
    string CollectionName,
    long NumEntities,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> MetadataSchema,
    IReadOnlyDictionary<string, object?> CollectionInfo);

public interface IVectorStoreManagement
{
    Task EnsureCollectionAsync(
        string collectionName,
        CancellationToken cancellationToken = default);

    Task<bool> CollectionExistsAsync(
        string collectionName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListDocumentNamesAsync(
        string collectionName,
        CancellationToken cancellationToken = default);

    Task DeleteDocumentsAsync(
        string collectionName,
        IReadOnlyList<string> documentNames,
        CancellationToken cancellationToken = default);

    Task DeleteCollectionAsync(
        string collectionName,
        CancellationToken cancellationToken = default);

    Task<bool> CheckHealthAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorStoreCollectionDetails>> ListCollectionsAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<VectorStoreCollectionDetails>>([]);

    Task CompactCollectionAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
