namespace DotnetRag.Shared.Abstractions;

public interface IObjectStore
{
    bool IsEnabled { get; }
    string BackendName { get; }

    Task StoreJsonAsync(
        string collectionName,
        string objectName,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListAsync(
        string collectionName,
        string prefix,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string collectionName,
        IReadOnlyList<string> objectNames,
        CancellationToken cancellationToken = default);

    Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default);
}
