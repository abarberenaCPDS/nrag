namespace DotnetRag.Shared.Summarization;

// ORIG: nvidia_rag/utils/summarization.py::generate_document_summaries (entry point)
public interface ISummarizationService
{
    /// <summary>Summarize a batch of documents and persist results to the vector store.</summary>
    Task<SummarizationStats> GenerateDocumentSummariesAsync(
        IReadOnlyList<DocumentContent> documents,
        string collectionName,
        SummarizationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieve a previously stored summary from the vector store. Returns null if not found.</summary>
    Task<string?> GetSummaryTextAsync(
        string collectionName,
        string fileName,
        CancellationToken cancellationToken = default);
}

/// <summary>A document's extracted text ready for summarization.</summary>
public sealed record DocumentContent(string FileName, string Text);

/// <summary>Controls strategy, page filtering, and shallow mode for a summarization run.</summary>
public sealed record SummarizationOptions(
    SummarizationStrategy Strategy = SummarizationStrategy.Iterative,
    // null | string "even"/"odd" | System.Text.Json.JsonElement [[start,end],...]
    object? PageFilter = null,
    bool IsShallow = false);

public enum SummarizationStrategy { Single, Iterative, Hierarchical }

public sealed record SummarizationStats(
    int TotalFiles,
    int Successful,
    int Failed,
    double DurationSeconds,
    IReadOnlyDictionary<string, FileSummaryResult> Files);

public sealed record FileSummaryResult(
    string FileName,
    string Status,      // "SUCCESS" | "FAILED"
    double Duration,
    string? Error = null);
