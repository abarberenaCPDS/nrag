using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Models;

namespace DotnetRag.Rag.Services;

// ORIG: nvidia_rag/rag_server/main.py — query rewriting branch inside _rag_chain() / search()
// Rewrites the user's current question into a standalone form that doesn't
// require the surrounding chat history to be understood by the retriever.
public sealed class QueryRewritingService(
    IChatCompletionService chatService,
    RagServerConfiguration config,
    ILogger<QueryRewritingService> logger)
{
    private const string SystemPrompt =
        "Given a chat history and the latest user question which might reference context " +
        "in the chat history, formulate a standalone question which can be understood " +
        "without the chat history. Do NOT answer the question, just reformulate it if needed " +
        "and otherwise return it as is. Return only the reformulated question, no explanation.";

    // Returns the rewritten query, or the original query if rewriting is not
    // applicable (no history, or rewriting disabled).
    public async Task<string> RewriteAsync(
        string query,
        IReadOnlyList<Message> conversationHistory,
        CancellationToken cancellationToken = default)
    {
        if (conversationHistory.Count == 0 || config.ConversationHistory == 0)
        {
            return query;
        }

        var messages = new List<ChatMessage> { new("system", SystemPrompt) };

        // Include the last N turns of history (excluding the current message)
        var historyWindow = config.ConversationHistory > 0
            ? conversationHistory.SkipLast(1).TakeLast(config.ConversationHistory).ToList()
            : conversationHistory.SkipLast(1).ToList();

        foreach (var msg in historyWindow)
        {
            var text = msg.Content is string s ? s : msg.Content?.ToString() ?? string.Empty;
            messages.Add(new ChatMessage(msg.Role, text));
        }

        messages.Add(new ChatMessage("human", query));

        var request = new ChatCompletionRequest(
            Model: config.LlmModel,
            Messages: messages,
            Temperature: 0.0,
            TopP: 0.1,
            MaxTokens: 256);

        try
        {
            var response = await chatService.CompleteAsync(request, cancellationToken);
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
            logger.LogWarning(ex, "Query rewriting failed; using original query.");
            return query;
        }
    }
}
