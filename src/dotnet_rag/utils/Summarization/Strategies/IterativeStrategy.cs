using DotnetRag.Shared.Abstractions;

namespace DotnetRag.Shared.Summarization.Strategies;

// ORIG: nvidia_rag/utils/summarization.py::_summarize_iterative
// Sequential chunk processing: chunk 0 → initial summary, chunks 1..n → rolling update.
public sealed class IterativeStrategy(
    IChatCompletionService chat,
    SummarizationPrompts prompts,
    string model,
    double temperature,
    int maxTokens,
    int maxChunkLength,
    int chunkOverlap) : ISummarizationStrategy
{
    public async Task<string> SummarizeAsync(
        string text,
        string fileName,
        Func<int, int, Task>? progressCallback,
        bool isShallow,
        CancellationToken cancellationToken)
    {
        var (systemInitial, humanInitial) = isShallow
            ? (prompts.ShallowSummarySystem, prompts.ShallowSummaryHuman)
            : (prompts.DocumentSummarySystem, prompts.DocumentSummaryHuman);

        // Fits in one chunk — single pass
        if (TextSplitter.EstimateTokens(text) <= maxChunkLength)
        {
            if (progressCallback is not null) await progressCallback(0, 1);

            var resp = await chat.CompleteAsync(new ChatCompletionRequest(
                Model: model,
                Messages: [
                    new ChatMessage("system", systemInitial),
                    new ChatMessage("user", humanInitial.Replace("{document_text}", text))
                ],
                Temperature: temperature,
                MaxTokens: maxTokens), cancellationToken);

            if (progressCallback is not null) await progressCallback(1, 1);
            return resp.Content.Trim();
        }

        var chunks = TextSplitter.Split(text, maxChunkLength, chunkOverlap);
        if (progressCallback is not null) await progressCallback(0, chunks.Count);

        // First chunk → initial summary
        var initialResp = await chat.CompleteAsync(new ChatCompletionRequest(
            Model: model,
            Messages: [
                new ChatMessage("system", systemInitial),
                new ChatMessage("user", humanInitial.Replace("{document_text}", chunks[0]))
            ],
            Temperature: temperature,
            MaxTokens: maxTokens), cancellationToken);

        var summary = initialResp.Content.Trim();
        if (progressCallback is not null) await progressCallback(1, chunks.Count);

        // Subsequent chunks → iterative rolling update
        for (int i = 1; i < chunks.Count; i++)
        {
            var userContent = prompts.IterativeSummaryHuman
                .Replace("{previous_summary}", summary)
                .Replace("{new_chunk}", chunks[i]);

            var iterResp = await chat.CompleteAsync(new ChatCompletionRequest(
                Model: model,
                Messages: [
                    new ChatMessage("system", prompts.IterativeSummarySystem),
                    new ChatMessage("user", userContent)
                ],
                Temperature: temperature,
                MaxTokens: maxTokens), cancellationToken);

            summary = iterResp.Content.Trim();
            if (progressCallback is not null) await progressCallback(i + 1, chunks.Count);
        }

        return summary;
    }
}
