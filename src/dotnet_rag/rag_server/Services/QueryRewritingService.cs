using System.Diagnostics;
using DotnetRag.Rag.Observability;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Models;
using DotnetRag.Shared.Prompts;

namespace DotnetRag.Rag.Services;

// ORIG: nvidia_rag/rag_server/main.py — query rewriting branch inside _rag_chain() / search()
// Rewrites the user's current question into a standalone form that doesn't
// require the surrounding chat history to be understood by the retriever.
public sealed class QueryRewritingService(
    IChatCompletionService chatService,
    RagServerConfiguration config,
    PromptCatalog prompts,
    ILogger<QueryRewritingService> logger)
{
    // Returns the rewritten query, or the original query if rewriting is not
    // applicable (no history, or rewriting disabled).
    public async Task<string> RewriteAsync(
        string query,
        IReadOnlyList<Message> conversationHistory,
        CancellationToken cancellationToken = default,
        string? modelOverride = null)
    {
        if (conversationHistory.Count == 0 || config.ConversationHistory == 0)
        {
            return query;
        }

        // Include the last N turns of history (excluding the current message)
        var historyWindow = config.ConversationHistory > 0
            ? conversationHistory.SkipLast(1).TakeLast(config.ConversationHistory).ToList()
            : conversationHistory.SkipLast(1).ToList();

        var chatHistory = string.Join("\n", historyWindow.Select(msg =>
        {
            var text = msg.Content is string s ? s : msg.Content?.ToString() ?? string.Empty;
            return $"{msg.Role}: {text}";
        }));

        var prompt = prompts.QueryRewriterPrompt;
        var messages = new List<ChatMessage>
        {
            new("system", PromptCatalog.Render(prompt.System, new Dictionary<string, string?>
            {
                ["chat_history"] = chatHistory,
                ["input"] = query
            })),
            new("human", PromptCatalog.Render(prompt.Human, new Dictionary<string, string?>
            {
                ["chat_history"] = chatHistory,
                ["input"] = query
            }))
        };


        var request = new ChatCompletionRequest(
            Model: string.IsNullOrWhiteSpace(modelOverride)
                ? config.QueryRewriterModelOrDefault
                : modelOverride,
            Messages: messages,
            Temperature: 0.0,
            TopP: 0.1,
            MaxTokens: 256);

        using var activity = RagMetrics.ActivitySource.StartActivity("rag.Query Rewriting.token_usage");
        activity?.SetTag("rag.prompt.template", "query_rewriter_prompt");
        activity?.SetTag("rag.prompt.message_count", messages.Count);
        activity?.SetTag("rag.query_rewriting.history_message_count", historyWindow.Count);
        activity?.SetTag("gen_ai.request.model", request.Model);

        try
        {
            var response = await chatService.CompleteAsync(request, cancellationToken);
            RagTraceAttributes.SetLlmUsageTags(activity, response.Usage);
            var rewritten = response.Content?.Trim();

            if (string.IsNullOrEmpty(rewritten))
            {
                return query;
            }

            logger.LogInformation(
                "Query rewritten: '{Original}' → '{Rewritten}'",
                query,
                rewritten);

            return rewritten;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogWarning(ex, "Query rewriting failed; using original query.");
            return query;
        }
    }
}
