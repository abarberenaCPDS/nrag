namespace DotnetRag.Shared.Abstractions;

public interface IChatCompletionClientFactory
{
    IChatCompletionService Create(
        string provider,
        string model,
        string endpoint,
        string? apiKey = null);
}
