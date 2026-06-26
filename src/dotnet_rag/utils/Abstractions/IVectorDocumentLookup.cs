namespace DotnetRag.Shared.Abstractions;

public interface IVectorDocumentLookup
{
    Task<string?> GetDocumentTextByIdAsync(
        string collectionName,
        string id,
        CancellationToken cancellationToken = default);
}
