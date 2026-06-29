using DotnetRag.Ingestor.Models;

namespace DotnetRag.Ingestor.Services;

public sealed class ExternalIngestionPipeline(string backendName) : IIngestionPipeline
{
    public string BackendName { get; } = backendName;
    public bool SupportsMultimodalExtraction => true;
    public bool SupportsObjectStoreAssets => true;

    public Task<IngestionPipelineResult> ExtractAsync(
        string path,
        string filename,
        ExtractionOptions? extractionOptions = null,
        CancellationToken cancellationToken = default)
    {
        throw BuildException();
    }

    public Task<string> ExtractTextAsync(
        string path,
        string filename,
        CancellationToken cancellationToken = default)
    {
        throw BuildException();
    }

    private NotSupportedException BuildException() =>
        new(
            $"Ingestion backend '{BackendName}' is selected, but the .NET adapter is not implemented yet. "
            + "Set APP_INGESTION_ENDPOINT to an HTTP extraction bridge or use APP_INGESTION_BACKEND=local.");
}
