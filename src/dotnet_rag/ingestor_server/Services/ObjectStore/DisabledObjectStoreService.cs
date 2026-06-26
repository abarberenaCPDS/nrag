namespace DotnetRag.Ingestor.Services.ObjectStore;

public sealed class DisabledObjectStoreService : IObjectStoreService
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
