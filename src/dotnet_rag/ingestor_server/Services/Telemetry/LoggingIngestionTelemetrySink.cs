namespace DotnetRag.Ingestor.Services.Telemetry;

public sealed class LoggingIngestionTelemetrySink(
    ILogger<LoggingIngestionTelemetrySink> logger) : IIngestionTelemetrySink
{
    public void Checkpoint(
        string name,
        IReadOnlyDictionary<string, object?> attributes)
    {
        logger.LogInformation(
            "ingestion_checkpoint {Checkpoint} {@Attributes}",
            name,
            attributes);
    }
}
