using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;

namespace DotnetRag.Rag.Services;

// ORIG: nvidia_rag/rag_server/filter_generator.py
// Uses the LLM to produce a Milvus boolean filter expression from the user query
// and the collection's metadata schema. Only activated when EnableFilterGenerator=true
// and the vector store backend is Milvus.
public sealed class FilterExpressionService(
    IChatCompletionService chatService,
    IVectorStore vectorStore,
    RagServerConfiguration config,
    ILogger<FilterExpressionService> logger)
{
    private const string SystemPrompt =
        "You are a query filter expert. Given a user question and a Milvus collection schema, " +
        "determine whether a metadata filter expression would help retrieve more relevant documents. " +
        "If a useful filter can be derived, output ONLY the Milvus boolean expression (e.g. " +
        "metadata[\"filename\"] == \"report.pdf\" or metadata[\"year\"] > 2022). " +
        "If no filter is appropriate, output exactly: None";

    // Returns a Milvus filter expression, or null if no filter is applicable.
    // Only called when ENABLE_FILTER_GENERATOR=true and VectorStoreName=milvus.
    public async Task<string?> GenerateAsync(
        string query,
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        if (!config.EnableFilterGenerator || config.VectorStoreName != "milvus")
            return null;

        var schema = await vectorStore.GetSchemaDescriptionAsync(collectionName, cancellationToken);

        var userContent = string.IsNullOrWhiteSpace(schema)
            ? $"Query: {query}\n\nNo schema available. Output: None"
            : $"Query: {query}\n\nSchema:\n{schema}";

        var request = new ChatCompletionRequest(
            Model: config.LlmModel,
            Messages: [new("system", SystemPrompt), new("user", userContent)],
            Temperature: 0.0,
            TopP: 0.1,
            MaxTokens: 128);

        try
        {
            var response = await chatService.CompleteAsync(request, cancellationToken);
            var expr = response.Content?.Trim();

            if (string.IsNullOrEmpty(expr)
                || expr.Equals("None", StringComparison.OrdinalIgnoreCase)
                || expr.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            logger.LogInformation("Generated filter expression: {Expr}", expr);
            return expr;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Filter expression generation failed; proceeding without filter.");
            return null;
        }
    }
}
