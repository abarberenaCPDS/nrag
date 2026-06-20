namespace DotnetRag.Shared.Abstractions;

public interface IEmbeddingService
{
    Task<IReadOnlyList<float>> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default);
}
