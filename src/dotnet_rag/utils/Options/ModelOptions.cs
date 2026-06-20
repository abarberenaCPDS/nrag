namespace DotnetRag.Shared.Options;

public sealed class ModelOptions
{
    public string ChatModel { get; init; } = "nvidia/nemotron-3-super-120b-a12b";
    public string EmbeddingModel { get; init; } = "nvidia/llama-nemotron-embed-vl-1b-v2";
    public string RerankerModel { get; init; } = "nvidia/llama-nemotron-rerank-1b-v2";
    public string VlmModel { get; init; } = "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning";
    public string ChunkingModel { get; init; } = "intfloat/e5-large-unsupervised";
    public string? OllamaEndpoint { get; init; }
}
