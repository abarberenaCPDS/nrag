namespace DotnetRag.Shared.Summarization.Strategies;

// ORIG: nvidia_rag/utils/summarization.py::_summarize_single_pass / _summarize_iterative / _summarize_hierarchical
public interface ISummarizationStrategy
{
    /// <param name="progressCallback">Called with (current, total) after each chunk completes. May be null.</param>
    Task<string> SummarizeAsync(
        string text,
        string fileName,
        Func<int, int, Task>? progressCallback,
        bool isShallow,
        CancellationToken cancellationToken);
}
