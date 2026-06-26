using System.Collections.Concurrent;
using System.Text.Json;
using DotnetRag.Ingestor.Models;

namespace DotnetRag.Ingestor.Services;

public sealed class FileBackedIngestionTaskStore : IIngestionTaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _syncRoot = new();
    private readonly ConcurrentDictionary<string, IngestionTaskStatusResponse> _taskStates;

    public FileBackedIngestionTaskStore()
    {
        _path = Environment.GetEnvironmentVariable("APP_INGESTION_TASK_STORE_PATH")
            ?? Path.Combine(Path.GetTempPath(), "dotnet-rag-ingestion-tasks.json");
        _taskStates = Load(_path);
    }

    public void Set(string taskId, IngestionTaskStatusResponse status)
    {
        _taskStates[taskId] = status;
        Save();
    }

    public bool TryGet(string taskId, out IngestionTaskStatusResponse status)
    {
        return _taskStates.TryGetValue(taskId, out status!);
    }

    private static ConcurrentDictionary<string, IngestionTaskStatusResponse> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new ConcurrentDictionary<string, IngestionTaskStatusResponse>();
            }

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, IngestionTaskStatusResponse>>(
                json,
                JsonOptions);
            return new ConcurrentDictionary<string, IngestionTaskStatusResponse>(
                data ?? [],
                StringComparer.Ordinal);
        }
        catch
        {
            return new ConcurrentDictionary<string, IngestionTaskStatusResponse>();
        }
    }

    private void Save()
    {
        lock (_syncRoot)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(
                _taskStates.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value),
                JsonOptions);
            File.WriteAllText(_path, json);
        }
    }
}
