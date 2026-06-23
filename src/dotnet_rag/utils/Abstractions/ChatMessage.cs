namespace DotnetRag.Shared.Abstractions;

public sealed record ChatMessage(string Role, object Content);
