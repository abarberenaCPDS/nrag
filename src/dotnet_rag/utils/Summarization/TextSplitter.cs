#pragma warning disable SKEXP0050

using Microsoft.SemanticKernel.Text;

namespace DotnetRag.Shared.Summarization;

// ORIG: nvidia_rag/utils/summarization.py::_split_text_into_chunks / _token_length
// Uses SemanticKernel TextChunker (word-token approximation) — same library already used by DocumentChunker.
public static class TextSplitter
{
    /// <summary>
    /// Splits text into overlapping chunks suitable for iterative/hierarchical summarization.
    /// </summary>
    public static IReadOnlyList<string> Split(string text, int chunkSize, int chunkOverlap)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var lines = TextChunker.SplitPlainTextLines(text, chunkSize);
        return TextChunker.SplitPlainTextParagraphs(lines, chunkSize, chunkOverlap);
    }

    /// <summary>
    /// Word-count approximation of token length (matches SemanticKernel's internal counter).
    /// </summary>
    public static int EstimateTokens(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}
