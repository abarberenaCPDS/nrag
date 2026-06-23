using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;

namespace DotnetRag.Rag.Services;

// ORIG: nvidia_rag/rag_server/reflection.py
// Two-phase quality loop:
//   Phase 1 — Context Relevance: score retrieved context against the query;
//              if below threshold, rewrite the query and re-retrieve.
//   Phase 2 — Response Groundedness: score the generated response against
//              the context; if below threshold, regenerate the response.
public sealed class ReflectionService(
    IChatCompletionService chatService,
    RagServerConfiguration config,
    ILogger<ReflectionService> logger)
{
    private const string ContextRelevanceSystemPrompt =
        "You are a relevance grader. Given a user question and a set of retrieved document passages, " +
        "assess how well the passages address the question. " +
        "Respond with a single integer: 2 (highly relevant), 1 (partially relevant), or 0 (not relevant). " +
        "Output only the integer.";

    private const string QueryRewriteSystemPrompt =
        "Given a user question and the reason why retrieved documents were not relevant, " +
        "rewrite the question to improve document retrieval. " +
        "Return only the rewritten question, no explanation.";

    private const string GroundednessSystemPrompt =
        "You are a groundedness grader. Given a context and a response, assess whether the response " +
        "is well-grounded in the context. " +
        "Respond with a single integer: 2 (fully grounded), 1 (partially grounded), or 0 (not grounded). " +
        "Output only the integer.";

    private const string RegenerationSystemPrompt =
        "You are a helpful assistant. Using only the provided context, answer the user's question. " +
        "If the context does not contain sufficient information, say so clearly.";

    // Phase 1: Check if retrieved context is relevant to the query.
    // Returns (isRelevant, rewrittenQuery). rewrittenQuery is non-null when
    // a retry with a different query is recommended.
    public async Task<(bool IsRelevant, string? RewrittenQuery)> CheckContextRelevanceAsync(
        string query,
        string context,
        CancellationToken cancellationToken = default)
    {
        var score = await ScoreAsync(
            ContextRelevanceSystemPrompt,
            $"Question: {query}\n\nContext:\n{context}",
            cancellationToken);

        if (score >= config.ReflectionContextThreshold)
        {
            return (true, null);
        }

        logger.LogInformation(
            "Context relevance score {Score} below threshold {Threshold}; rewriting query.",
            score,
            config.ReflectionContextThreshold);

        var rewritten = await RewriteForRetrievalAsync(query, score, cancellationToken);
        return (false, rewritten);
    }

    // Phase 2: Check if the response is grounded in the retrieved context.
    // Returns (isGrounded, improvedResponse). improvedResponse is non-null
    // when regeneration produced a better answer.
    public async Task<(bool IsGrounded, string? ImprovedResponse)> CheckResponseGroundednessAsync(
        string query,
        string context,
        string response,
        CancellationToken cancellationToken = default)
    {
        var score = await ScoreAsync(
            GroundednessSystemPrompt,
            $"Context:\n{context}\n\nResponse:\n{response}",
            cancellationToken);

        if (score >= config.ReflectionGroundednessThreshold)
        {
            return (true, null);
        }

        logger.LogInformation(
            "Groundedness score {Score} below threshold {Threshold}; regenerating response.",
            score,
            config.ReflectionGroundednessThreshold);

        var regenerated = await RegenerateResponseAsync(query, context, cancellationToken);
        return (false, regenerated);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<int> ScoreAsync(
        string systemPrompt,
        string userContent,
        CancellationToken cancellationToken)
    {
        var request = new ChatCompletionRequest(
            Model: config.LlmModel,
            Messages: [new("system", systemPrompt), new("user", userContent)],
            Temperature: 0.0,
            TopP: 0.1,
            MaxTokens: 8);

        try
        {
            var response = await chatService.CompleteAsync(request, cancellationToken);
            var text = response.Content?.Trim() ?? string.Empty;

            if (text.Contains('2')) return 2;
            if (text.Contains('1')) return 1;
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reflection scoring failed; assuming passing score.");
            return config.ReflectionContextThreshold;
        }
    }

    private async Task<string?> RewriteForRetrievalAsync(
        string query,
        int score,
        CancellationToken cancellationToken)
    {
        var reason = score == 0
            ? "The retrieved documents were completely irrelevant."
            : "The retrieved documents were only partially relevant.";

        var request = new ChatCompletionRequest(
            Model: config.LlmModel,
            Messages:
            [
                new("system", QueryRewriteSystemPrompt),
                new("user", $"Original question: {query}\nReason: {reason}")
            ],
            Temperature: 0.0,
            TopP: 0.1,
            MaxTokens: 256);

        try
        {
            var response = await chatService.CompleteAsync(request, cancellationToken);
            return response.Content?.Trim();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reflection query rewrite failed.");
            return null;
        }
    }

    private async Task<string?> RegenerateResponseAsync(
        string query,
        string context,
        CancellationToken cancellationToken)
    {
        var request = new ChatCompletionRequest(
            Model: config.LlmModel,
            Messages:
            [
                new("system", RegenerationSystemPrompt),
                new("user", $"Context:\n{context}\n\nQuestion: {query}")
            ],
            Temperature: config.Temperature,
            TopP: config.TopP,
            MaxTokens: config.MaxTokens);

        try
        {
            var response = await chatService.CompleteAsync(request, cancellationToken);
            return response.Content?.Trim();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reflection response regeneration failed.");
            return null;
        }
    }
}
