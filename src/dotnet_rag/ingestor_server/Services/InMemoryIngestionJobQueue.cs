using System.Collections.Concurrent;

namespace DotnetRag.Ingestor.Services;

public sealed class InMemoryIngestionJobQueue : IIngestionJobQueue
{
    private readonly ConcurrentQueue<IngestionJob> _jobs = new();

    public bool IsDurable => false;

    public Task EnqueueAsync(
        IngestionJob job,
        CancellationToken cancellationToken = default)
    {
        _jobs.Enqueue(job);
        return Task.CompletedTask;
    }

    public Task<IngestionJob?> TryClaimAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_jobs.TryDequeue(out var job) ? job : null);
    }

    public Task CompleteAsync(
        IngestionJob job,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task FailAsync(
        IngestionJob job,
        string error,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
