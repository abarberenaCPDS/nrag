namespace DotnetRag.Shared.Abstractions;

public sealed record VectorStoreClient(
    IVectorStore Store,
    IVectorStoreManagement Management,
    IVectorStoreFilterCapabilities? FilterCapabilities = null);

public interface IVectorStoreClientFactory
{
    VectorStoreClient Create(
        string? endpoint = null,
        string? bearerToken = null,
        string? embeddingEndpoint = null,
        string? embeddingModel = null);
}
