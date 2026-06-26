namespace DotnetRag.Shared.Abstractions;

public sealed record VectorStoreClient(
    IVectorStore Store,
    IVectorStoreManagement Management);

public interface IVectorStoreClientFactory
{
    VectorStoreClient Create(
        string? endpoint = null,
        string? bearerToken = null);
}
