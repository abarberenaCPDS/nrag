using System.Text.Json;

namespace DotnetRag.Ingestor.Services.ObjectStore;

public sealed class FileObjectStoreService : IObjectStoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _root;

    public FileObjectStoreService()
    {
        _root = Environment.GetEnvironmentVariable("APP_OBJECT_STORE_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "dotnet-rag-object-store");
    }

    public bool IsEnabled => true;
    public string BackendName => "filesystem";

    public async Task StoreJsonAsync(
        string collectionName,
        string objectName,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(collectionName, objectName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(payload, JsonOptions),
            cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListAsync(
        string collectionName,
        string prefix,
        CancellationToken cancellationToken = default)
    {
        var collectionDir = ResolveCollectionDirectory(collectionName);
        if (!Directory.Exists(collectionDir))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var normalizedPrefix = NormalizeObjectName(prefix);
        var files = Directory.EnumerateFiles(collectionDir, "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(collectionDir, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(name => name.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(files);
    }

    public Task DeleteAsync(
        string collectionName,
        IReadOnlyList<string> objectNames,
        CancellationToken cancellationToken = default)
    {
        foreach (var objectName in objectNames)
        {
            var path = ResolvePath(collectionName, objectName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(_root);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private string ResolvePath(string collectionName, string objectName) =>
        Path.Combine(ResolveCollectionDirectory(collectionName), NormalizeObjectName(objectName));

    private string ResolveCollectionDirectory(string collectionName) =>
        Path.Combine(_root, NormalizePathSegment(collectionName));

    private static string NormalizeObjectName(string value) =>
        value.Trim().TrimStart('/').Replace('\\', '/');

    private static string NormalizePathSegment(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return value;
    }
}
