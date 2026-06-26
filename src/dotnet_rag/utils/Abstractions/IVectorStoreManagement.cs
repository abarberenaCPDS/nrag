namespace DotnetRag.Shared.Abstractions;

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

    Task CompactCollectionAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
