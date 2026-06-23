using DotnetRag.Shared.Models;

namespace DotnetRag.Shared.Abstractions;

public interface IRerankerProvider
{
    string Name { get; }

    Task<IReadOnlyList<RerankChunkResult>> RerankAsync(
        RerankRequest request,
        CancellationToken cancellationToken = default);
}
