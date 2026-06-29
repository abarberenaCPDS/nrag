using DotnetRag.Shared.Abstractions;
using Microsoft.Extensions.Logging;

namespace DotnetRag.Shared.LlmProviders;

public sealed class ChatCompletionClientFactory(
    IHttpClientFactory httpClientFactory,
    ILogger<OllamaChatCompletionService> ollamaLogger,
    ILogger<OpenAiChatCompletionService> openAiLogger) : IChatCompletionClientFactory
{
    public IChatCompletionService Create(
        string provider,
        string model,
        string endpoint,
        string? apiKey = null)
    {
        if (provider.Trim().Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return new OllamaChatCompletionService(
                httpClientFactory,
                model,
                NormalizeOllamaBase(endpoint),
                ollamaLogger);
        }

        return new OpenAiChatCompletionService(
            httpClientFactory,
            model,
            endpoint,
            string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
            openAiLogger);
    }

    private static string NormalizeOllamaBase(string url)
    {
        var normalized = url.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"http://{normalized}";
        }

        foreach (var suffix in new[]
        {
            "/v1/chat/completions",
            "/v1/embeddings",
            "/api/generate",
            "/api/chat",
            "/api/embed",
            "/api/embeddings",
            "/v1"
        })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }

        return normalized.Contains(':')
            ? normalized.TrimEnd('/')
            : "http://localhost:11434";
    }
}
