using DotnetRag.Shared.Abstractions;

namespace DotnetRag.Shared.Summarization.Strategies;

// ORIG: nvidia_rag/utils/summarization.py::_summarize_hierarchical
// Parallel chunk summarization followed by iterative tree-reduction until one summary remains.
public sealed class HierarchicalStrategy(
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

        // Parallel: summarize every chunk independently
        var chunkSummaries = await Task.WhenAll(chunks.Select((chunk, i) =>
            SummarizeChunkAsync(chunk, systemInitial, humanInitial, cancellationToken)));

        if (progressCallback is not null) await progressCallback(chunks.Count, chunks.Count);

        // Tree-reduce: batch summaries until a single summary remains
        var current = chunkSummaries.ToList();
        while (current.Count > 1)
        {
            var batches = BatchByLength(current, maxChunkLength);
            var reduced = await Task.WhenAll(batches.Select(b =>
                CombineBatchAsync(b, cancellationToken)));
            current = [..reduced];
        }

        return current[0];
    }

    private async Task<string> SummarizeChunkAsync(
        string chunk, string system, string humanTemplate, CancellationToken ct)
    {
        var resp = await chat.CompleteAsync(new ChatCompletionRequest(
            Model: model,
            Messages: [
                new ChatMessage("system", system),
                new ChatMessage("user", humanTemplate.Replace("{document_text}", chunk))
            ],
            Temperature: temperature,
            MaxTokens: maxTokens), ct);

        return resp.Content.Trim();
    }

    private async Task<string> CombineBatchAsync(
        IReadOnlyList<string> batch, CancellationToken ct)
    {
        if (batch.Count == 1) return batch[0];

        var running = batch[0];
        for (int i = 1; i < batch.Count; i++)
        {
            var userContent = prompts.IterativeSummaryHuman
                .Replace("{previous_summary}", running)
                .Replace("{new_chunk}", batch[i]);

            var resp = await chat.CompleteAsync(new ChatCompletionRequest(
                Model: model,
                Messages: [
                    new ChatMessage("system", prompts.IterativeSummarySystem),
                    new ChatMessage("user", userContent)
                ],
                Temperature: temperature,
                MaxTokens: maxTokens), ct);

            running = resp.Content.Trim();
        }

        return running;
    }

    // ORIG: nvidia_rag/utils/summarization.py::_batch_summaries_by_length
    private static List<List<string>> BatchByLength(
        IReadOnlyList<string> summaries, int maxChunkLength)
    {
        var batches = new List<List<string>>();
        var current = new List<string>();
        int currentLength = 0;

        foreach (var s in summaries)
        {
            int len = TextSplitter.EstimateTokens(s);
            if (current.Count > 0 && currentLength + len > maxChunkLength)
            {
                batches.Add(current);
                current = [];
                currentLength = 0;
            }

            current.Add(s);
            currentLength += len;
        }

        if (current.Count > 0) batches.Add(current);
        return batches;
    }
}
