using System.Collections.Concurrent;

namespace DotnetRag.Shared.Summarization;

// ORIG: nvidia_rag/utils/summary_status_handler.py::SUMMARY_STATUS_HANDLER (Redis-backed)
// In-process replacement using ConcurrentDictionary — sufficient for single-server .NET deployment.
public sealed class SummaryProgressTracker
{
    private readonly ConcurrentDictionary<string, SummaryProgress> _store = new();

    public void UpdateProgress(
        string collectionName,
        string fileName,
        string status,
        ProgressInfo? progress = null,
        string? error = null)
    {
        var key = BuildKey(collectionName, fileName);
        _store[key] = new SummaryProgress(
            Status: status,
            Progress: progress,
            Error: error,
            StartedAt: _store.TryGetValue(key, out var existing) ? existing.StartedAt : DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    public SummaryProgress? GetProgress(string collectionName, string fileName)
    {
        _store.TryGetValue(BuildKey(collectionName, fileName), out var progress);
        return progress;
    }

    private static string BuildKey(string collection, string file) => $"{collection}::{file}";
}

public sealed record SummaryProgress(
    string Status,            // "IN_PROGRESS" | "SUCCESS" | "FAILED"
    ProgressInfo? Progress,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProgressInfo(int Current, int Total, string Message);
