namespace DotnetRag.Ingestor.Services;

public sealed class IngestionWorkerService(
    IIngestionJobQueue queue,
    IIngestionTaskStore taskStore,
    IngestorService ingestorService,
    ILogger<IngestionWorkerService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Ingestion worker started. queue_durable={Durable}",
            queue.IsDurable);

        while (!stoppingToken.IsCancellationRequested)
        {
            var job = await queue.TryClaimAsync(stoppingToken);
            if (job is null)
            {
                await Task.Delay(IdleDelay, stoppingToken);
                continue;
            }

            await ExecuteJobAsync(job, stoppingToken);
        }
    }

    private async Task ExecuteJobAsync(IngestionJob job, CancellationToken cancellationToken)
    {
        var initialStatus = IngestionTaskHandler.BuildInitialNvIngestStatus(job.FilePaths);
        taskStore.Set(job.TaskId, new Models.IngestionTaskStatusResponse
        {
            State = "IN_PROGRESS",
            Result = new Models.UploadDocumentResponse
            {
                Message = "Ingestion task is running."
            },
            NvIngestStatus = initialStatus
        });

        try
        {
            var result = await ingestorService.ExecuteQueuedIngestionAsync(job, cancellationToken);
            taskStore.Set(job.TaskId, new Models.IngestionTaskStatusResponse
            {
                State = "FINISHED",
                Result = result,
                NvIngestStatus = IngestionTaskHandler.BuildNvIngestStatus(result)
            });
            await queue.CompleteAsync(job, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Queued ingestion job {TaskId} failed.", job.TaskId);
            taskStore.Set(job.TaskId, new Models.IngestionTaskStatusResponse
            {
                State = "FAILED",
                Result = new Models.UploadDocumentResponse
                {
                    Message = ex.Message
                },
                NvIngestStatus = initialStatus
            });
            await queue.FailAsync(job, ex.Message, cancellationToken);
        }
    }
}
