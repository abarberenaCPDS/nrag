namespace DotnetRag.Shared.Abstractions;

public sealed record VectorDocument(
    string Id,
    string Text,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<float>? Embedding = null);
