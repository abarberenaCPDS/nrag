using System.Diagnostics;
using DotnetRag.Rag.Observability;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Prompts;

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
    PromptCatalog prompts,
    ILogger<ReflectionService> logger)
{
    // Phase 1: Check if retrieved context is relevant to the query.
    // Returns (isRelevant, rewrittenQuery). rewrittenQuery is non-null when
    // a retry with a different query is recommended.
    public async Task<(bool IsRelevant, string? RewrittenQuery)> CheckContextRelevanceAsync(
        string query,
        string context,
        CancellationToken cancellationToken = default,
        string? modelOverride = null)
    {
        var prompt = prompts.ReflectionRelevanceCheckPrompt;
        var score = await ScoreAsync(prompt, new Dictionary<string, string?>
        {
            ["query"] = query,
            ["context"] = context
        },
        "rag.Self Reflection.context_relevance.token_usage",
        "context_relevance",
        "reflection_relevance_check_prompt",
        modelOverride,
        cancellationToken);

        if (score >= config.ReflectionContextThreshold)
        {
            return (true, null);
        }

        logger.LogInformation(
            "Context relevance score {Score} below threshold {Threshold}; rewriting query.",
            score,
            config.ReflectionContextThreshold);

        var rewritten = await RewriteForRetrievalAsync(query, score, modelOverride, cancellationToken);
        return (false, rewritten);
    }

    // Phase 2: Check if the response is grounded in the retrieved context.
    // Returns (isGrounded, improvedResponse). improvedResponse is non-null
    // when regeneration produced a better answer.
    public async Task<(bool IsGrounded, string? ImprovedResponse)> CheckResponseGroundednessAsync(
        string query,
        string context,
        string response,
        CancellationToken cancellationToken = default,
        string? modelOverride = null)
    {
        var prompt = prompts.ReflectionGroundednessCheckPrompt;
        var score = await ScoreAsync(prompt, new Dictionary<string, string?>
        {
            ["context"] = context,
            ["response"] = response
        },
        "rag.Self Reflection.response_groundedness.token_usage",
        "response_groundedness",
        "reflection_groundedness_check_prompt",
        modelOverride,
        cancellationToken);

        if (score >= config.ReflectionGroundednessThreshold)
        {
            return (true, null);
        }

        logger.LogInformation(
            "Groundedness score {Score} below threshold {Threshold}; regenerating response.",
            score,
            config.ReflectionGroundednessThreshold);

        var regenerated = await RegenerateResponseAsync(query, context, modelOverride, cancellationToken);
        return (false, regenerated);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<int> ScoreAsync(
        PromptSection prompt,
        IReadOnlyDictionary<string, string?> values,
        string spanName,
        string step,
        string promptTemplate,
        string? modelOverride,
        CancellationToken cancellationToken)
    {
        var request = new ChatCompletionRequest(
            Model: string.IsNullOrWhiteSpace(modelOverride)
                ? config.ReflectionModelOrDefault
                : modelOverride,
            Messages:
            [
                new("system", PromptCatalog.Render(prompt.System, values)),
                new("user", PromptCatalog.Render(prompt.Human, values))
            ],
            Temperature: 0.0,
            TopP: 0.1,
            MaxTokens: 8);

        try
        {
            var response = await CompleteStageAsync(
                request,
                spanName,
                step,
                promptTemplate,
                cancellationToken);
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
        string? modelOverride,
        CancellationToken cancellationToken)
    {
        var reason = score == 0
            ? "The retrieved documents were completely irrelevant."
            : "The retrieved documents were only partially relevant.";

        var prompt = prompts.ReflectionQueryRewriterPrompt;
        var request = new ChatCompletionRequest(
            Model: string.IsNullOrWhiteSpace(modelOverride)
                ? config.ReflectionModelOrDefault
                : modelOverride,
            Messages:
            [
                new("system", PromptCatalog.Render(prompt.System, new Dictionary<string, string?>
                {
                    ["query"] = query,
                    ["reason"] = reason
                })),
                new("user", PromptCatalog.Render(prompt.Human, new Dictionary<string, string?>
                {
                    ["query"] = query,
                    ["reason"] = reason
                }))
            ],
            Temperature: 0.0,
            TopP: 0.1,
            MaxTokens: 256);

        try
        {
            var response = await CompleteStageAsync(
                request,
                "rag.Self Reflection.query_rewrite.token_usage",
                "query_rewrite",
                "reflection_query_rewriter_prompt",
                cancellationToken);
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
        string? modelOverride,
        CancellationToken cancellationToken)
    {
        var prompt = prompts.ReflectionResponseRegenerationPrompt;
        var request = new ChatCompletionRequest(
            Model: string.IsNullOrWhiteSpace(modelOverride)
                ? config.ReflectionModelOrDefault
                : modelOverride,
            Messages:
            [
                new("system", PromptCatalog.Render(prompt.System, new Dictionary<string, string?>
                {
                    ["query"] = query,
                    ["context"] = context
                })),
                new("user", PromptCatalog.Render(prompt.Human, new Dictionary<string, string?>
                {
                    ["query"] = query,
                    ["context"] = context
                }))
            ],
            Temperature: config.Temperature,
            TopP: config.TopP,
            MaxTokens: config.MaxTokens);

        try
        {
            var response = await CompleteStageAsync(
                request,
                "rag.Self Reflection.response_regeneration.token_usage",
                "response_regeneration",
                "reflection_response_regeneration_prompt",
                cancellationToken);
            return response.Content?.Trim();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reflection response regeneration failed.");
            return null;
        }
    }

    private async Task<ChatCompletionResponse> CompleteStageAsync(
        ChatCompletionRequest request,
        string spanName,
        string step,
        string promptTemplate,
        CancellationToken cancellationToken)
    {
        using var activity = RagMetrics.ActivitySource.StartActivity(spanName);
        activity?.SetTag("rag.reflection.step", step);
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
}
