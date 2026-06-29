using DotnetRag.Shared.Abstractions;

namespace DotnetRag.Rag.Services;

public sealed record CitationAsset(string ContentBase64, string DocumentType);

public interface ICitationAssetResolver
{
    Task<CitationAsset?> ResolveAsync(
        VectorSearchResult result,
        CancellationToken cancellationToken = default);
}
