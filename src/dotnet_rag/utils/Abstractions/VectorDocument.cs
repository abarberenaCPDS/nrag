namespace DotnetRag.Shared.Abstractions;

public sealed record VectorDocument(
    string Id,
    string Text,
    IReadOnlyDictionary<string, object?>? Metadata = null,
    IReadOnlyList<float>? Embedding = null);
