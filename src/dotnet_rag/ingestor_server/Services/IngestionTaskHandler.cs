using System.Collections.Concurrent;
using DotnetRag.Ingestor.Models;

namespace DotnetRag.Ingestor.Services;

public sealed class IngestionTaskHandler
{
    private readonly ConcurrentDictionary<string, IngestionTaskStatusResponse> _taskStates = new();

    public async Task<string> SubmitTask(
        Func<string, Task<UploadDocumentResponse>> taskFactory,
        NvIngestStatusResponse initialNvIngestStatus,
        string? taskId = null)
    {
        var resolvedTaskId = string.IsNullOrWhiteSpace(taskId)
            ? Guid.NewGuid().ToString()
            : taskId;

        _taskStates[resolvedTaskId] = new IngestionTaskStatusResponse
        {
            State = "PENDING",
            Result = new UploadDocumentResponse(),
            NvIngestStatus = initialNvIngestStatus
        };


        try
        {
            var result = await taskFactory(resolvedTaskId);
            _taskStates[resolvedTaskId] = new IngestionTaskStatusResponse
            {
                State = "FINISHED",
                Result = result,
                NvIngestStatus = BuildNvIngestStatus(result)
            };
        }
        catch (Exception ex)
        {
            _taskStates[resolvedTaskId] = new IngestionTaskStatusResponse
            {
                State = "FAILED",
                Result = new UploadDocumentResponse
                {
                    Message = ex.Message
                },
                NvIngestStatus = initialNvIngestStatus
            };
        }

        return resolvedTaskId;
    }

    public IngestionTaskStatusResponse GetTaskStatusAndResult(string taskId)
    {
        return _taskStates.TryGetValue(taskId, out var state)
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

    private static NvIngestStatusResponse BuildNvIngestStatus(UploadDocumentResponse result)
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
