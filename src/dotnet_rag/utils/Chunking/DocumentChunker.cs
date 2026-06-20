#pragma warning disable SKEXP0050 // TextChunker is experimental but stable enough for production use

using Microsoft.SemanticKernel.Text;

namespace DotnetRag.Shared.Chunking;

/// <summary>
/// Text chunking utilities wrapping Microsoft.SemanticKernel.Text.TextChunker.
/// ORIG_CHUNKER: langchain-text-splitters + intfloat/e5-large-unsupervised tokenizer
/// </summary>
public static class DocumentChunker
{
    /// <summary>
    /// Splits plain text into overlapping chunks suitable for embedding and retrieval.
    /// </summary>
    /// <param name="text">Source text.</param>
    /// <param name="maxTokensPerLine">Max tokens per chunk line (default 512).</param>
    /// <param name="maxTokensPerParagraph">Max tokens per assembled paragraph chunk (default 1024).</param>
    /// <param name="overlapTokens">Overlap between consecutive chunks (default 100).</param>
    public static IReadOnlyList<string> ChunkText(
        string text,
        int maxTokensPerLine = 512,
        int maxTokensPerParagraph = 1024,
        int overlapTokens = 100)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        // First pass: split into lines capped at maxTokensPerLine
        var lines = TextChunker.SplitPlainTextLines(text, maxTokensPerLine);

        // Second pass: assemble lines into paragraphs with overlap
        var paragraphs = TextChunker.SplitPlainTextParagraphs(
            lines,
            maxTokensPerParagraph,
            overlapTokens);

        return paragraphs;
    }

    /// <summary>
    /// Splits a markdown document into chunks, preserving header structure.
    /// Useful for ingesting README and documentation files.
    /// </summary>
    public static IReadOnlyList<string> ChunkMarkdown(
        string markdown,
        int maxTokensPerLine = 512,
        int maxTokensPerParagraph = 1024,
        int overlapTokens = 100)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var lines = TextChunker.SplitMarkDownLines(markdown, maxTokensPerLine);
        return TextChunker.SplitMarkdownParagraphs(lines, maxTokensPerParagraph, overlapTokens);
    }
}
