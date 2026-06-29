namespace DotnetRag.Shared.Abstractions;

public sealed record ChatCompletionRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    bool EnableThinking = false,
    int? MaxTokens = null,
    double? Temperature = null,
    double? TopP = null,
    int? ThinkingTokenBudget = null);
