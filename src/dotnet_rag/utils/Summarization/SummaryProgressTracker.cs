using System.Collections.Concurrent;
using System.Text.Json;

namespace DotnetRag.Shared.Summarization;

// ORIG: nvidia_rag/utils/summary_status_handler.py::SUMMARY_STATUS_HANDLER (Redis-backed)
// Interface-backed replacement: in-memory by default, file-backed when cross-process
// API/worker status sharing is required.
public interface ISummaryProgressStore
{
    SummaryProgress? Get(string collectionName, string fileName);

    void Set(string collectionName, string fileName, SummaryProgress progress);
}

public sealed class InMemorySummaryProgressStore : ISummaryProgressStore
{
    private readonly ConcurrentDictionary<string, SummaryProgress> _store = new();

    public SummaryProgress? Get(string collectionName, string fileName)
    {
        _store.TryGetValue(BuildKey(collectionName, fileName), out var progress);
        return progress;
    }

    public void Set(string collectionName, string fileName, SummaryProgress progress)
        => _store[BuildKey(collectionName, fileName)] = progress;

    internal static string BuildKey(string collection, string file) => $"{collection}::{file}";
}

public sealed class FileSummaryProgressStore(string path) : ISummaryProgressStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _gate = new();

    public SummaryProgress? Get(string collectionName, string fileName)
    {
        lock (_gate)
        {
            var store = ReadAll();
            return store.TryGetValue(InMemorySummaryProgressStore.BuildKey(collectionName, fileName), out var progress)
                ? progress
                : null;
        }
    }

    public void Set(string collectionName, string fileName, SummaryProgress progress)
    {
        lock (_gate)
        {
            var store = ReadAll();
            store[InMemorySummaryProgressStore.BuildKey(collectionName, fileName)] = progress;
            WriteAll(store);
        }
    }

    private Dictionary<string, SummaryProgress> ReadAll()
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, SummaryProgress>(StringComparer.Ordinal);
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, SummaryProgress>>(json, JsonOptions)
                ?? new Dictionary<string, SummaryProgress>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, SummaryProgress>(StringComparer.Ordinal);
        }
    }

    private void WriteAll(Dictionary<string, SummaryProgress> store)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(store, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }
}

public sealed class SummaryProgressTracker(ISummaryProgressStore? store = null)
{
    private readonly ISummaryProgressStore _store = store ?? new InMemorySummaryProgressStore();

    public void UpdateProgress(
        string collectionName,
        string fileName,
        string status,
        ProgressInfo? progress = null,
        string? error = null)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = _store.Get(collectionName, fileName);
        _store.Set(collectionName, fileName, new SummaryProgress(
            Status: status,
            Progress: progress,
            Error: error,
            StartedAt: existing is not null ? existing.StartedAt : now,
            UpdatedAt: now,
            CompletedAt: IsTerminalStatus(status) ? now : null));
    }

    public SummaryProgress? GetProgress(string collectionName, string fileName) =>
        _store.Get(collectionName, fileName);

    private static bool IsTerminalStatus(string status) =>
        status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase)
        || status.Equals("FAILED", StringComparison.OrdinalIgnoreCase);
}

public sealed record SummaryProgress(
    string Status,            // "IN_PROGRESS" | "SUCCESS" | "FAILED"
    ProgressInfo? Progress,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt = null);

public sealed record ProgressInfo(int Current, int Total, string Message);
