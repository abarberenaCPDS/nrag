namespace DotnetRag.Ingestor.Services;

public static class IngestionPipelineFactory
{
    public static IIngestionPipeline Create(IHttpClientFactory httpClientFactory)
    {
        var backend = Environment.GetEnvironmentVariable("APP_INGESTION_BACKEND")
            ?.Trim()
            .ToLowerInvariant();
        var endpoint = ResolveEndpoint(backend);

        return backend switch
        {
            "nvingest" or "nv-ingest" when endpoint is not null =>
                new HttpExternalIngestionPipeline(
                    httpClientFactory.CreateClient(),
                    "nvingest",
                    endpoint,
                    Environment.GetEnvironmentVariable("APP_INGESTION_API_KEY")),
            "nvingest" or "nv-ingest" => new ExternalIngestionPipeline("nvingest"),
            "nrl" when endpoint is not null =>
                new HttpExternalIngestionPipeline(
                    httpClientFactory.CreateClient(),
                    "nrl",
                    endpoint,
                    Environment.GetEnvironmentVariable("APP_INGESTION_API_KEY")),
            "nrl" => new ExternalIngestionPipeline("nrl"),
            _ => new LocalIngestionPipeline()
        };
    }

    private static Uri? ResolveEndpoint(string? backend)
    {
        var endpoint = backend switch
        {
            "nvingest" or "nv-ingest" =>
                Environment.GetEnvironmentVariable("APP_NVINGEST_ENDPOINT")
                ?? Environment.GetEnvironmentVariable("APP_INGESTION_ENDPOINT"),
            "nrl" =>
                Environment.GetEnvironmentVariable("APP_NRL_ENDPOINT")
                ?? Environment.GetEnvironmentVariable("APP_INGESTION_ENDPOINT"),
            _ => null
        };

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            ? NormalizeBridgeEndpoint(uri)
            : null;
    }

    private static Uri NormalizeBridgeEndpoint(Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            return uri;
        }

        return new Uri(uri, "/bridge/extract");
    }
}
