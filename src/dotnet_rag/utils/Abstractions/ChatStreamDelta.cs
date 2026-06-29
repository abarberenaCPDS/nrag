namespace DotnetRag.Shared.Abstractions;

public sealed record ChatStreamDelta(
    string? Content = null,
    string? ReasoningContent = null,
    IReadOnlyDictionary<string, object?>? Usage = null);
