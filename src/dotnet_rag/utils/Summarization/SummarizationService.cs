using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Prompts;
using DotnetRag.Shared.Summarization.Strategies;
using Microsoft.Extensions.Logging;

namespace DotnetRag.Shared.Summarization;

// ORIG: nvidia_rag/utils/summarization.py::generate_document_summaries + _process_single_file_summary
// Stores summaries in provider-selected vector collection "summary_{collectionName}" and object store when enabled.
public sealed class SummarizationService(
    IChatCompletionService chat,
    IVectorStore vectorStore,
    IVectorStoreManagement vectorStoreManagement,
    IVectorDocumentLookup documentLookup,
    IObjectStore objectStore,
    RagServerConfiguration config,
    PromptCatalog promptCatalog,
    SummarizationRateLimiter rateLimiter,
    SummaryProgressTracker progressTracker,
    ILogger<SummarizationService> logger) : ISummarizationService
{
    private static readonly string SummaryIdPrefix = "summary_";
    private static readonly string SummaryCollectionPrefix = "summary_";

    public async Task<SummarizationStats> GenerateDocumentSummariesAsync(
        IReadOnlyList<DocumentContent> documents,
        string collectionName,
        SummarizationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SummarizationOptions();
        var prompts = SummarizationPrompts.FromCatalog(promptCatalog);
        var started = DateTime.UtcNow;

        logger.LogInformation(
            "Starting summarization for {Count} document(s) in collection '{Collection}' [strategy={Strategy}]",
            documents.Count, collectionName, options.Strategy);

        var summaryCollection = $"{SummaryCollectionPrefix}{collectionName}";
        await vectorStoreManagement.EnsureCollectionAsync(summaryCollection, cancellationToken);

        var tasks = documents
            .Where(d => !string.IsNullOrWhiteSpace(d.Text))
            .Select(doc => ProcessFileAsync(doc, collectionName, summaryCollection, prompts, options, cancellationToken));

        var results = await Task.WhenAll(tasks);

        var stats = new SummarizationStats(
            TotalFiles: documents.Count,
            Successful: results.Count(r => r.Status == "SUCCESS"),
            Failed: results.Count(r => r.Status == "FAILED"),
            DurationSeconds: (DateTime.UtcNow - started).TotalSeconds,
            Files: results.ToDictionary(r => r.FileName));

        logger.LogInformation(
            "Summarization complete: {Successful}/{Total} succeeded in {Duration:F1}s",
            stats.Successful, stats.TotalFiles, stats.DurationSeconds);

        return stats;
    }

    // ORIG: nvidia_rag/utils/summarization.py::_store_summary_in_object_store + TryGetCollectionIdAsync
    public async Task<string?> GetSummaryTextAsync(
        string collectionName,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var summaryCollection = $"{SummaryCollectionPrefix}{collectionName}";
        var summaryId = $"{SummaryIdPrefix}{fileName}";
        return await documentLookup.GetDocumentTextByIdAsync(summaryCollection, summaryId, cancellationToken);
    }

    // ── Per-file pipeline ─────────────────────────────────────────────────────

    private async Task<FileSummaryResult> ProcessFileAsync(
        DocumentContent doc,
        string collectionName,
        string summaryCollection,
        SummarizationPrompts prompts,
        SummarizationOptions options,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;

        progressTracker.UpdateProgress(collectionName, doc.FileName, "IN_PROGRESS",
            new ProgressInfo(0, 0, "Queued..."));

        await rateLimiter.WaitAsync(cancellationToken);
        try
        {
            var strategy = BuildStrategy(options.Strategy, prompts);

            async Task OnProgress(int current, int total) =>
                progressTracker.UpdateProgress(collectionName, doc.FileName, "IN_PROGRESS",
                    new ProgressInfo(current, total, $"Processing chunk {current}/{total}"));

            var summary = await strategy.SummarizeAsync(
                doc.Text, doc.FileName, OnProgress, options.IsShallow, cancellationToken);

            await StoreSummaryAsync(summaryCollection, doc.FileName, collectionName, summary, cancellationToken);

            progressTracker.UpdateProgress(collectionName, doc.FileName, "SUCCESS");

            var duration = (DateTime.UtcNow - started).TotalSeconds;
            logger.LogInformation("Summary complete: {File} ({Duration:F1}s)", doc.FileName, duration);

            return new FileSummaryResult(doc.FileName, "SUCCESS", duration);
        }
        catch (Exception ex)
        {
            progressTracker.UpdateProgress(collectionName, doc.FileName, "FAILED", error: ex.Message);
            var duration = (DateTime.UtcNow - started).TotalSeconds;
            logger.LogError(ex, "Summary failed: {File}", doc.FileName);
            return new FileSummaryResult(doc.FileName, "FAILED", duration, ex.Message);
        }
        finally
        {
            rateLimiter.Release();
        }
    }

    // ORIG: nvidia_rag/utils/summarization.py::_store_summary_in_object_store
    private async Task StoreSummaryAsync(
        string summaryCollection,
        string fileName,
        string collectionName,
        string summaryText,
        CancellationToken ct)
    {
        var doc = new VectorDocument(
            Id: $"{SummaryIdPrefix}{fileName}",
            Text: summaryText,
            Metadata: new Dictionary<string, object?>
            {
                ["filename"] = fileName,
                ["collection_name"] = collectionName,
                ["type"] = "summary"
            });

        await vectorStore.UpsertAsync(summaryCollection, [doc], ct);
        if (objectStore.IsEnabled)
        {
            await objectStore.StoreJsonAsync(
                summaryCollection,
                fileName,
                new Dictionary<string, object?>
                {
                    ["filename"] = fileName,
                    ["collection_name"] = collectionName,
                    ["summary"] = summaryText
                },
                ct);
        }

        logger.LogDebug("Stored summary for '{File}' in collection '{Collection}'", fileName, summaryCollection);
    }

    // ── Strategy factory ──────────────────────────────────────────────────────

    private ISummarizationStrategy BuildStrategy(SummarizationStrategy strategy, SummarizationPrompts prompts)
    {
        var model = string.IsNullOrWhiteSpace(config.SummarizerModel) ? config.LlmModel : config.SummarizerModel;
        var temp = config.SummarizerTemperature;
        var maxTokens = config.MaxTokens;
        var chunkLen = config.SummarizerMaxChunkLength;
        var overlap = config.SummarizerChunkOverlap;

        return strategy switch
        {
            SummarizationStrategy.Single =>
                new SinglePassStrategy(chat, prompts, model, temp, maxTokens, chunkLen),
            SummarizationStrategy.Hierarchical =>
                new HierarchicalStrategy(chat, prompts, model, temp, maxTokens, chunkLen, overlap),
            _ =>
                new IterativeStrategy(chat, prompts, model, temp, maxTokens, chunkLen, overlap)
        };
    }
}
