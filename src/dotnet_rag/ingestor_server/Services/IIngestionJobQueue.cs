namespace DotnetRag.Ingestor.Services;

public interface IIngestionJobQueue
{
    bool IsDurable { get; }
    Task EnqueueAsync(IngestionJob job, CancellationToken cancellationToken = default);
    Task<IngestionJob?> TryClaimAsync(CancellationToken cancellationToken = default);
    Task CompleteAsync(IngestionJob job, CancellationToken cancellationToken = default);
    Task FailAsync(IngestionJob job, string error, CancellationToken cancellationToken = default);
}
