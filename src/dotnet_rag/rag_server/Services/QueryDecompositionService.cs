using System.Diagnostics;
using System.Text.RegularExpressions;
using DotnetRag.Rag.Clients;
using DotnetRag.Rag.Observability;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Prompts;

namespace DotnetRag.Rag.Services;

public sealed record QueryDecompositionResult(
    IReadOnlyList<VectorSearchResult> Chunks,
    string ConversationHistory,
    string FinalSystemPrompt,
    string FinalHumanPrompt);

// ORIG: nvidia_rag/rag_server/query_decomposition.py
// YAML-backed iterative decomposition path: generate sub-queries, answer each
// with retrieved context, optionally ask a follow-up prompt, then build final
// answer prompt inputs from the collected sub-answers and merged contexts.
public sealed class QueryDecompositionService(
    IChatCompletionService chatService,
    IVectorStore vectorStore,
    IRerankerClient rerankerClient,
    RagServerConfiguration config,
    PromptCatalog prompts,
    ILogger<QueryDecompositionService> logger)
{
    private static readonly Regex ListPrefixRegex = new(@"^\s*(?:\d+[\.)]|[-*])\s+", RegexOptions.Compiled);

    public async Task<QueryDecompositionResult> RunAsync(
        string query,
        string collectionName,
        int topK,
        int rerankerTopK,
        double threshold,
        bool shouldRerank,
        string? filterExpr,
        CancellationToken cancellationToken,
        IVectorStore? vectorStoreOverride = null,
        string? rerankerEndpoint = null,
        string? modelOverride = null)
    {
        var activeVectorStore = vectorStoreOverride ?? vectorStore;
        var subqueries = await GenerateSubqueriesAsync(query, cancellationToken, modelOverride);
        if (subqueries.Count == 0)
        {
            subqueries = [query];
        }

        if (subqueries.Count == 1)
        {
            logger.LogInformation("No decomposition needed; using direct RAG prompt for query decomposition request.");
            var directChunks = await RetrieveRankedAsync(
                query,
                query,
                collectionName,
                topK,
                rerankerTopK,
                threshold,
                shouldRerank,
                filterExpr,
                cancellationToken,
                activeVectorStore,
                rerankerEndpoint);
            return BuildFinalResult(query, [], directChunks);
        }

        var history = new List<(string Question, string Answer)>();
        var allChunks = new Dictionary<string, VectorSearchResult>(StringComparer.Ordinal);
        var activeQuestions = subqueries.ToList();
        var maxDepth = Math.Max(1, config.QueryDecompositionRecursionDepth);

        for (var depth = 0; depth < maxDepth && activeQuestions.Count > 0; depth++)
        {
            foreach (var subquery in activeQuestions)
            {
                var rewritten = await RewriteQueryWithContextAsync(subquery, history, cancellationToken, modelOverride);
                var chunks = await RetrieveRankedAsync(
                    rewritten,
                    query,
                    collectionName,
                    topK,
                    rerankerTopK,
                    threshold,
                    shouldRerank,
                    filterExpr,
                    cancellationToken,
                    activeVectorStore,
                    rerankerEndpoint);

                AddChunks(allChunks, chunks);
                var answer = await GenerateSubqueryAnswerAsync(rewritten, chunks, cancellationToken, modelOverride);
                history.Add((subquery, answer));
            }

            if (depth == maxDepth - 1)
            {
                break;
            }

            var followup = await GenerateFollowupQuestionAsync(query, history, allChunks.Values, cancellationToken, modelOverride);
            if (string.IsNullOrWhiteSpace(followup) || AlreadyTriedEmpty(followup, history))
            {
                break;
            }

            activeQuestions = [followup];
        }

        var originalQueryChunks = await RetrieveRankedAsync(
            query,
            query,
            collectionName,
            topK,
            rerankerTopK,
            threshold,
            shouldRerank,
            filterExpr,
            cancellationToken,
            activeVectorStore,
            rerankerEndpoint);
        AddChunks(allChunks, originalQueryChunks);

        var finalChunks = allChunks.Values
            .OrderByDescending(r => r.Score)
            .Take(rerankerTopK > 0 ? rerankerTopK : topK)
            .ToList();
        logger.LogInformation(
            "Query decomposition generated {AnswerCount} sub-answer(s) and merged {ChunkCount} chunks.",
            history.Count,
            finalChunks.Count);

        return BuildFinalResult(query, history, finalChunks);
    }

    private QueryDecompositionResult BuildFinalResult(
        string query,
        IReadOnlyList<(string Question, string Answer)> history,
        IReadOnlyList<VectorSearchResult> chunks)
    {
        var context = FormatChunks(chunks);
        var conversationHistory = FormatConversationHistory(history);
        var finalPrompt = prompts.QueryDecompositionFinalResponsePrompt;
        var values = new Dictionary<string, string?>
        {
            ["conversation_history"] = conversationHistory,
            ["context"] = context,
            ["question"] = query
        };

        return new QueryDecompositionResult(
            chunks,
            conversationHistory,
            PromptCatalog.Render(finalPrompt.System, values),
            PromptCatalog.Render(finalPrompt.Human, values));
    }

    public async Task<IReadOnlyList<VectorSearchResult>> RetrieveContextsAsync(
        string query,
        string collectionName,
        int topK,
        int rerankerTopK,
        double threshold,
        bool shouldRerank,
        string? filterExpr,
        CancellationToken cancellationToken)
        => (await RunAsync(
            query,
            collectionName,
            topK,
            rerankerTopK,
            threshold,
            shouldRerank,
            filterExpr,
            cancellationToken)).Chunks;

    public async Task<IReadOnlyList<string>> GenerateSubqueriesAsync(
        string query,
        CancellationToken cancellationToken = default,
        string? modelOverride = null)
    {
        var prompt = prompts.QueryDecompositionMultiqueryPrompt;
        var values = new Dictionary<string, string?> { ["question"] = query };
        var request = new ChatCompletionRequest(
            Model: string.IsNullOrWhiteSpace(modelOverride)
                ? config.QueryRewriterModelOrDefault
                : modelOverride,
            Messages:
            [
                new("system", PromptCatalog.Render(prompt.System, values)),
                new("user", PromptCatalog.Render(prompt.Human, values))
            ],
            Temperature: 0.0,
            TopP: 0.1,
            MaxTokens: 512);

        try
        {
            var response = await CompleteStageAsync(
                request,
                "generate_subqueries",
                "query_decomposition_multiquery_prompt",
                cancellationToken);
            var parsed = ParseSubqueries(response.Content)
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            return parsed.Count == 0 ? [query] : parsed;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Query decomposition failed; using original query.");
            return [query];
        }
    }

    private async Task<string> RewriteQueryWithContextAsync(
        string question,
        IReadOnlyList<(string Question, string Answer)> history,
        CancellationToken cancellationToken,
        string? modelOverride)
    {
        if (history.Count == 0)
        {
            return question;
        }

        var prompt = prompts.QueryDecompositionQueryRewriterPrompt;
        var values = new Dictionary<string, string?>
        {
            ["conversation_history"] = FormatConversationHistory(history),
            ["question"] = question
        };
        var request = new ChatCompletionRequest(
            Model: string.IsNullOrWhiteSpace(modelOverride)
                ? config.QueryRewriterModelOrDefault
                : modelOverride,
            Messages:
            [
                new("system", PromptCatalog.Render(prompt.System, values)),
                new("user", PromptCatalog.Render(prompt.Human, values))
            ],
            Temperature: 0.0,
            TopP: 0.1,
            MaxTokens: 256);

        try
        {
            var response = await CompleteStageAsync(
                request,
                "contextual_rewrite",
                "query_decompositions_query_rewriter_prompt",
                cancellationToken);
            return string.IsNullOrWhiteSpace(response.Content) ? question : response.Content.Trim();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Query decomposition contextual rewrite failed.");
            return question;
        }
    }

    private async Task<string> GenerateSubqueryAnswerAsync(
        string question,
        IReadOnlyList<VectorSearchResult> chunks,
        CancellationToken cancellationToken,
        string? modelOverride)
    {
        var prompt = prompts.QueryDecompositionRagTemplate;
        var values = new Dictionary<string, string?>
        {
            ["context"] = FormatChunks(chunks),
            ["question"] = question
        };
        var request = new ChatCompletionRequest(
            Model: string.IsNullOrWhiteSpace(modelOverride)
                ? config.QueryRewriterModelOrDefault
                : modelOverride,
            Messages:
            [
                new("system", PromptCatalog.Render(prompt.System, values)),
                new("user", PromptCatalog.Render(prompt.Human, values))
            ],
            Temperature: config.Temperature,
            TopP: config.TopP,
            MaxTokens: config.MaxTokens);

        try
        {
            var response = await CompleteStageAsync(
                request,
                "subquery_answer",
                "query_decomposition_rag_template",
                cancellationToken);
            return response.Content?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Query decomposition sub-query answer generation failed.");
            return string.Empty;
        }
    }

    private async Task<string> GenerateFollowupQuestionAsync(
        string query,
        IReadOnlyList<(string Question, string Answer)> history,
        IEnumerable<VectorSearchResult> chunks,
        CancellationToken cancellationToken,
        string? modelOverride)
    {
        var prompt = prompts.QueryDecompositionFollowupQuestionPrompt;
        var values = new Dictionary<string, string?>
        {
            ["conversation_history"] = FormatConversationHistory(history),
            ["context"] = FormatChunks(chunks),
            ["question"] = query
        };
        var request = new ChatCompletionRequest(
            Model: string.IsNullOrWhiteSpace(modelOverride)
                ? config.QueryRewriterModelOrDefault
                : modelOverride,
            Messages:
            [
                new("system", PromptCatalog.Render(prompt.System, values)),
                new("user", PromptCatalog.Render(prompt.Human, values))
            ],
            Temperature: 0.0,
            TopP: 0.1,
            MaxTokens: 256);

        try
        {
            var response = await CompleteStageAsync(
                request,
                "followup_question",
                "query_decomposition_followup_question_prompt",
                cancellationToken);
            return response.Content?.Trim().Trim('\'', '"') ?? string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Query decomposition follow-up generation failed.");
            return string.Empty;
        }
    }

    private async Task<IReadOnlyList<VectorSearchResult>> RetrieveRankedAsync(
        string retrievalQuery,
        string originalQuery,
        string collectionName,
        int topK,
        int rerankerTopK,
        double threshold,
        bool shouldRerank,
        string? filterExpr,
        CancellationToken cancellationToken,
        IVectorStore activeVectorStore,
        string? rerankerEndpoint)
    {
        try
        {
            var rawResults = await activeVectorStore.SearchAsync(
                collectionName, retrievalQuery, topK, filterExpr, cancellationToken);
            var candidates = rawResults
                .Select(r => WithCollectionMetadata(r, collectionName))
                .ToList();
            return await ApplyRerankingAsync(
                originalQuery,
                candidates,
                rerankerTopK,
                shouldRerank,
                cancellationToken,
                rerankerEndpoint,
                threshold);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Query decomposition retrieval failed for query '{Query}'.", retrievalQuery);
            return [];
        }
    }

    private async Task<ChatCompletionResponse> CompleteStageAsync(
        ChatCompletionRequest request,
        string step,
        string promptTemplate,
        CancellationToken cancellationToken)
    {
        using var activity = RagMetrics.ActivitySource.StartActivity("rag.Query Decomposition.token_usage");
        activity?.SetTag("rag.query_decomposition.step", step);
        activity?.SetTag("rag.prompt.template", promptTemplate);
        activity?.SetTag("rag.prompt.message_count", request.Messages.Count);
        activity?.SetTag("gen_ai.request.model", request.Model);

        try
        {
            var response = await chatService.CompleteAsync(request, cancellationToken);
            RagTraceAttributes.SetLlmUsageTags(activity, response.Usage);
            return response;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    public static IReadOnlyList<string> ParseSubqueries(string? content)
    {
        var prefixed = new List<string>();
        var unprefixed = new List<string>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (ListPrefixRegex.IsMatch(line))
            {
                var parsed = ListPrefixRegex.Replace(line, string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(parsed))
                {
                    prefixed.Add(parsed);
                }
            }
            else
            {
                unprefixed.Add(line);
            }
        }

        return prefixed.Count > 0 ? prefixed : unprefixed;
    }

    private static void AddChunks(
        IDictionary<string, VectorSearchResult> merged,
        IEnumerable<VectorSearchResult> chunks)
    {
        foreach (var result in chunks)
        {
            var key = string.IsNullOrWhiteSpace(result.Id) ? result.Text : result.Id;
            if (!merged.ContainsKey(key))
            {
                merged[key] = result;
            }
        }
    }

    private static bool AlreadyTriedEmpty(
        string question,
        IEnumerable<(string Question, string Answer)> history)
    {
        var normalized = question.Trim();
        return history.Any(h =>
            string.Equals(h.Question.Trim(), normalized, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(h.Answer.Trim('\'', '"')));
    }

    public static string FormatConversationHistory(IEnumerable<(string Question, string Answer)> history)
        => string.Join("\n\n\n", history.Select(item =>
            $"Question: {item.Question}\nAnswer: {item.Answer}"));

    private static string FormatChunks(IEnumerable<VectorSearchResult> chunks)
        => string.Join("\n---\n", chunks.Select(c => c.Text));

    private static VectorSearchResult WithCollectionMetadata(
        VectorSearchResult result,
        string collectionName)
    {
        var metadata = result.Metadata?.ToDictionary(kv => kv.Key, kv => kv.Value)
            ?? new Dictionary<string, string>();
        metadata["collection_name"] = collectionName;
        return result with { Metadata = metadata };
    }

    private async Task<IReadOnlyList<VectorSearchResult>> ApplyRerankingAsync(
        string query,
        IReadOnlyList<VectorSearchResult> candidates,
        int topK,
        bool shouldRerank,
        CancellationToken cancellationToken,
        string? rerankerEndpoint,
        double confidenceThreshold)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var normalizedTopK = topK > 0 ? topK : config.RerankerTopK;
        if (!shouldRerank)
        {
            if (confidenceThreshold > 0.0)
            {
                logger.LogWarning(
                    "confidence_threshold is set to {Threshold} during query decomposition but reranking is disabled. Returning vector-score ordering without confidence filtering.",
                    confidenceThreshold);
            }

            return candidates
                .OrderByDescending(c => c.Score)
                .Take(normalizedTopK)
                .ToList();
        }

        try
        {
            var reranked = await rerankerClient.RerankAsync(
                query,
                candidates,
                normalizedTopK,
                cancellationToken,
                rerankerEndpoint);
            var normalized = NormalizeRerankerScores(reranked);
            if (confidenceThreshold <= 0.0)
            {
                return normalized;
            }

            var filtered = normalized
                .Where(r => r.Score >= confidenceThreshold)
                .ToList();
            logger.LogInformation(
                "Applied query decomposition confidence threshold {Threshold}: {InputCount} -> {OutputCount}.",
                confidenceThreshold,
                normalized.Count,
                filtered.Count);
            return filtered;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reranker unavailable during query decomposition.");
            if (confidenceThreshold > 0.0)
            {
                logger.LogWarning(
                    "confidence_threshold is set to {Threshold} during query decomposition but reranker scores are unavailable. Returning vector-score ordering without confidence filtering.",
                    confidenceThreshold);
            }

            return candidates
                .OrderByDescending(c => c.Score)
                .Take(normalizedTopK)
                .ToList();
        }
    }

    private static IReadOnlyList<VectorSearchResult> NormalizeRerankerScores(
        IReadOnlyList<VectorSearchResult> reranked)
    {
        if (reranked.Count == 0
            || reranked.All(result => result.Score is >= 0.0 and <= 1.0))
        {
            return reranked;
        }

        return reranked
            .Select(result => result with
            {
                Score = 1.0 / (1.0 + Math.Exp(-(result.Score * 0.1)))
            })
            .ToList();
    }
}
