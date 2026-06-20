namespace DotnetRag.Shared.Options;

public sealed class VectorStoreOptions
{
    public string Provider { get; init; } = "chroma";
    public string Endpoint { get; init; } = "http://localhost:8000";
    public string CollectionName { get; init; } = "multimodal_data";
    public string Tenant { get; init; } = "default_tenant";
    public string Database { get; init; } = "default_database";
}
