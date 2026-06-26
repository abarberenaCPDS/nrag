using System.Text.Json;

namespace DotnetRag.Ingestor.Services;

public sealed class FileBackedIngestionJobQueue : IIngestionJobQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _root;

    public FileBackedIngestionJobQueue()
    {
        _root = Environment.GetEnvironmentVariable("APP_INGESTION_JOB_QUEUE_PATH")
            ?? Path.Combine(Path.GetTempPath(), "dotnet-rag-ingestion-jobs");
        Directory.CreateDirectory(PendingDirectory);
        Directory.CreateDirectory(ProcessingDirectory);
        Directory.CreateDirectory(CompletedDirectory);
        Directory.CreateDirectory(FailedDirectory);
    }

    public bool IsDurable => true;

    private string PendingDirectory => Path.Combine(_root, "pending");
    private string ProcessingDirectory => Path.Combine(_root, "processing");
    private string CompletedDirectory => Path.Combine(_root, "completed");
    private string FailedDirectory => Path.Combine(_root, "failed");

    public async Task EnqueueAsync(
        IngestionJob job,
        CancellationToken cancellationToken = default)
    {
        var pendingPath = JobPath(PendingDirectory, job.TaskId);
        var tempPath = $"{pendingPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(
            tempPath,
            JsonSerializer.Serialize(job, JsonOptions),
            cancellationToken);
        File.Move(tempPath, pendingPath, overwrite: true);
    }

    public async Task<IngestionJob?> TryClaimAsync(CancellationToken cancellationToken = default)
    {
        foreach (var pendingPath in Directory
            .EnumerateFiles(PendingDirectory, "*.json")
            .OrderBy(File.GetCreationTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processingPath = Path.Combine(ProcessingDirectory, Path.GetFileName(pendingPath));
            try
            {
                File.Move(pendingPath, processingPath);
            }
            catch (IOException)
            {
                continue;
            }

            try
            {
                var json = await File.ReadAllTextAsync(processingPath, cancellationToken);
                return JsonSerializer.Deserialize<IngestionJob>(json, JsonOptions);
            }
            catch
            {
                File.Move(processingPath, Path.Combine(FailedDirectory, Path.GetFileName(processingPath)), overwrite: true);
                continue;
            }
        }

        return null;
    }

    public Task CompleteAsync(
        IngestionJob job,
        CancellationToken cancellationToken = default)
    {
        MoveFromProcessing(job.TaskId, CompletedDirectory);
        return Task.CompletedTask;
    }

    public async Task FailAsync(
        IngestionJob job,
        string error,
        CancellationToken cancellationToken = default)
    {
        var processingPath = JobPath(ProcessingDirectory, job.TaskId);
        if (File.Exists(processingPath))
        {
            var failedPath = JobPath(FailedDirectory, job.TaskId);
            File.Move(processingPath, failedPath, overwrite: true);
            await File.WriteAllTextAsync(
                $"{failedPath}.error.txt",
                error,
                cancellationToken);
        }
    }

    private void MoveFromProcessing(string taskId, string targetDirectory)
    {
        var processingPath = JobPath(ProcessingDirectory, taskId);
        if (!File.Exists(processingPath))
        {
            return;
        }

        File.Move(processingPath, JobPath(targetDirectory, taskId), overwrite: true);
    }

    private static string JobPath(string directory, string taskId) =>
        Path.Combine(directory, $"{SafeTaskId(taskId)}.json");

    private static string SafeTaskId(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return value;
    }
}
