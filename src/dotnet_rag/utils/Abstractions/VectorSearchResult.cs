namespace DotnetRag.Shared.Abstractions;

public sealed record VectorSearchResult(
    string Id,
    string Text,
    double Score,
    IReadOnlyDictionary<string, string>? Metadata = null);
