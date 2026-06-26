using System.Collections.Concurrent;
using DotnetRag.Ingestor.Models;

namespace DotnetRag.Ingestor.Services;

public sealed class InMemoryIngestionTaskStore : IIngestionTaskStore
{
    private readonly ConcurrentDictionary<string, IngestionTaskStatusResponse> _taskStates = new();

    public void Set(string taskId, IngestionTaskStatusResponse status)
    {
        _taskStates[taskId] = status;
    }

    public bool TryGet(string taskId, out IngestionTaskStatusResponse status)
    {
        return _taskStates.TryGetValue(taskId, out status!);
    }
}
