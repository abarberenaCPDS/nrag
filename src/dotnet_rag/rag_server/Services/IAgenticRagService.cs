using DotnetRag.Shared.Models;

namespace DotnetRag.Rag.Services;

public sealed record AgenticRagInvocation(
    Prompt Prompt,
    string RequestPath,
    bool IsStreaming,
    string? UserQuery,
    IReadOnlyList<string> CollectionNames,
    string? VdbEndpoint,
    string? EmbeddingEndpoint,
    string? EmbeddingModel,
    string? LlmEndpoint,
    string? Model,
    string? BearerToken)
{
    public static AgenticRagInvocation From(HttpRequest request, Prompt prompt)
    {
        var userQuery = prompt.Messages
            .LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            ?.Content
            ?.ToString();
        var requestPath = request.Path.Value ?? string.Empty;
        return new AgenticRagInvocation(
            prompt,
            requestPath,
            !requestPath.Contains("chat/completions", StringComparison.OrdinalIgnoreCase),
            userQuery,
            prompt.CollectionNames ?? [],
            prompt.VdbEndpoint,
            prompt.EmbeddingEndpoint,
            prompt.EmbeddingModel,
            prompt.LlmEndpoint,
            prompt.Model,
            GetBearerToken(request));
    }

    public bool HasProviderOverrides =>
        !string.IsNullOrWhiteSpace(VdbEndpoint)
        || !string.IsNullOrWhiteSpace(EmbeddingEndpoint)
        || !string.IsNullOrWhiteSpace(EmbeddingModel)
        || !string.IsNullOrWhiteSpace(LlmEndpoint)
        || !string.IsNullOrWhiteSpace(Model)
        || !string.IsNullOrWhiteSpace(BearerToken);

    private static string? GetBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }
}

public interface IAgenticRagService
{
    bool IsRequested(Prompt prompt);

    Task<IResult> GenerateAsync(
        AgenticRagInvocation invocation,
        CancellationToken cancellationToken = default);
}
