namespace DotnetRag.Shared.Options;

public sealed class ModelOptions
{
    public string ChatModel { get; init; } = "";
    public string EmbeddingModel { get; init; } = "";
    public string RerankerModel { get; init; } = "";
    public string VlmModel { get; init; } = "";
    public string ChunkingModel { get; init; } = "";
    public string? OllamaEndpoint { get; init; }
}
