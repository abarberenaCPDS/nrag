using DotnetRag.Ingestor.Models;

namespace DotnetRag.Ingestor.Services;

public interface IIngestionTaskStore
{
    void Set(string taskId, IngestionTaskStatusResponse status);
    bool TryGet(string taskId, out IngestionTaskStatusResponse status);
}
