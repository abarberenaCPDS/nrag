namespace DotnetRag.Ingestor.Services.Telemetry;

public interface IIngestionTelemetrySink
{
    void Checkpoint(
        string name,
        IReadOnlyDictionary<string, object?> attributes);
}
