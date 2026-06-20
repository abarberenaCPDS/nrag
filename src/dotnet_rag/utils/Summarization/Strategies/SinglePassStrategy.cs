using DotnetRag.Shared.Abstractions;

namespace DotnetRag.Shared.Summarization.Strategies;

// ORIG: nvidia_rag/utils/summarization.py::_summarize_single_pass
// Sends the full document (truncated to maxChunkLength if necessary) in one LLM call.
public sealed class SinglePassStrategy(
    IChatCompletionService chat,
    SummarizationPrompts prompts,
    string model,
    double temperature,
    int maxTokens,
    int maxChunkLength) : ISummarizationStrategy
{
    public async Task<string> SummarizeAsync(
        string text,
        string fileName,
        Func<int, int, Task>? progressCallback,
        bool isShallow,
        CancellationToken cancellationToken)
    {
        // Truncate if the document exceeds the context window
        if (TextSplitter.EstimateTokens(text) > maxChunkLength)
        {
            var words = text.Split(' ');
            text = string.Join(' ', words.Take(maxChunkLength));
        }

        var (systemPrompt, humanTemplate) = isShallow
            ? (prompts.ShallowSummarySystem, prompts.ShallowSummaryHuman)
            : (prompts.DocumentSummarySystem, prompts.DocumentSummaryHuman);

        var userContent = humanTemplate.Replace("{document_text}", text);

        if (progressCallback is not null) await progressCallback(0, 1);

        var response = await chat.CompleteAsync(new ChatCompletionRequest(
            Model: model,
            Messages: [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userContent)
            ],
            Temperature: temperature,
            MaxTokens: maxTokens), cancellationToken);

        if (progressCallback is not null) await progressCallback(1, 1);
        return response.Content.Trim();
    }
}
