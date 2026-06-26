using System.Text.Json;

namespace DotnetRag.Ingestor.Services;

public sealed class FileBackedIngestorCatalogStore : IIngestorCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _syncRoot = new();

    public FileBackedIngestorCatalogStore()
    {
        _path = Environment.GetEnvironmentVariable("APP_INGESTOR_CATALOG_PATH")
            ?? Path.Combine(Path.GetTempPath(), "dotnet-rag-catalog.json");
    }

    public IReadOnlyList<IngestorCatalogEntry> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<IngestorCatalogEntry>>(
                    json,
                    JsonOptions)
                ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<IngestorCatalogEntry> entries)
    {
        lock (_syncRoot)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(
                    _path,
                    JsonSerializer.Serialize(
                        entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
                        JsonOptions));
            }
            catch
            {
                // Keep API behavior available even when optional persistence fails.
            }
        }
    }
}
