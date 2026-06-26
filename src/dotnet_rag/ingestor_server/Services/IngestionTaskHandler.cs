using DotnetRag.Ingestor.Models;

namespace DotnetRag.Ingestor.Services;

public sealed class IngestionTaskHandler(IIngestionTaskStore taskStore)
{
    public Task<string> SubmitTask(
        Func<string, Task<UploadDocumentResponse>> taskFactory,
        NvIngestStatusResponse initialNvIngestStatus,
        string? taskId = null)
    {
        var resolvedTaskId = string.IsNullOrWhiteSpace(taskId)
            ? Guid.NewGuid().ToString()
            : taskId;

        taskStore.Set(resolvedTaskId, new IngestionTaskStatusResponse
        {
            State = "PENDING",
            Result = new UploadDocumentResponse(),
            NvIngestStatus = initialNvIngestStatus
        });

        // Fire and forget — returns the task ID immediately so the HTTP response
        // is not blocked. State transitions (PENDING → FINISHED/FAILED) happen
        // on the background thread and are visible via GET /status.
        _ = Task.Run(async () =>
        {
            try
            {
                taskStore.Set(resolvedTaskId, new IngestionTaskStatusResponse
                {
                    State = "IN_PROGRESS",
                    Result = new UploadDocumentResponse
                    {
                        Message = "Ingestion task is running."
                    },
                    NvIngestStatus = initialNvIngestStatus
                });

                var result = await taskFactory(resolvedTaskId);
                taskStore.Set(resolvedTaskId, new IngestionTaskStatusResponse
                {
                    State = "FINISHED",
                    Result = result,
                    NvIngestStatus = BuildNvIngestStatus(result)
                });
            }
            catch (Exception ex)
            {
                taskStore.Set(resolvedTaskId, new IngestionTaskStatusResponse
                {
                    State = "FAILED",
                    Result = new UploadDocumentResponse
                    {
                        Message = ex.Message
                    },
                    NvIngestStatus = initialNvIngestStatus
                });
            }
        });

        return Task.FromResult(resolvedTaskId);
    }

    public IngestionTaskStatusResponse GetTaskStatusAndResult(string taskId)
    {
        return taskStore.TryGet(taskId, out var state)
            ? state
            : new IngestionTaskStatusResponse
            {
                State = "UNKNOWN",
                Result = new UploadDocumentResponse
                {
                    Message = $"Task '{taskId}' not found"
                },
                NvIngestStatus = new NvIngestStatusResponse()
            };
    }

    public void SetPending(string taskId, NvIngestStatusResponse initialNvIngestStatus)
    {
        taskStore.Set(taskId, new IngestionTaskStatusResponse
        {
            State = "PENDING",
            Result = new UploadDocumentResponse(),
            NvIngestStatus = initialNvIngestStatus
        });
    }

    public static NvIngestStatusResponse BuildInitialNvIngestStatus(IEnumerable<string> filepaths)
    {
        var status = filepaths.ToDictionary(
            path => Path.GetFileName(path),
            _ => "not_started",
            StringComparer.OrdinalIgnoreCase);

        return new NvIngestStatusResponse
        {
            ExtractionCompleted = 0,
            DocumentWiseStatus = status
        };
    }

    public static NvIngestStatusResponse BuildNvIngestStatus(UploadDocumentResponse result)
    {
        var status = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in result.Documents)
        {
            status[doc.DocumentName] = "completed";
        }

        foreach (var failed in result.FailedDocuments)
        {
            status[failed.DocumentName] = "failed";
        }

        return new NvIngestStatusResponse
        {
            ExtractionCompleted = result.Documents.Count,
            DocumentWiseStatus = status
        };
    }
}
