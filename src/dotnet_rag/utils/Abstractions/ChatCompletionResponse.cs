namespace DotnetRag.Shared.Abstractions;

public sealed record ChatCompletionResponse(
    string Content,
    string? Reasoning = null,
    IReadOnlyDictionary<string, object?>? Usage = null);
