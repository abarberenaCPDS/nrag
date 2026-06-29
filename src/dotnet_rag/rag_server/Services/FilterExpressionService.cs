using System.Diagnostics;
using DotnetRag.Rag.Observability;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Prompts;

namespace DotnetRag.Rag.Services;

// ORIG: nvidia_rag/rag_server/filter_generator.py
// Uses the LLM to produce a provider-specific filter expression from the user
// query and collection metadata schema. Concrete vector stores advertise support
// through IVectorStoreFilterCapabilities.
public sealed class FilterExpressionService(
    IChatCompletionService chatService,
    IVectorStoreFilterCapabilities filterCapabilities,
    RagServerConfiguration config,
    PromptCatalog prompts,
    ILogger<FilterExpressionService> logger)
{
    // Returns a provider-specific filter expression, or null if no filter applies
    // or the active vector store does not support generated filters.
    public async Task<string?> GenerateAsync(
        string query,
        string collectionName,
        bool forceEnable = false,
        IVectorStoreFilterCapabilities? capabilitiesOverride = null,
        string? modelOverride = null,
        CancellationToken cancellationToken = default)
    {
        var capabilities = capabilitiesOverride ?? filterCapabilities;
        if ((!forceEnable && !config.EnableFilterGenerator) || !capabilities.SupportsGeneratedFilters)
            return null;

        var schema = await capabilities.GetFilterSchemaDescriptionAsync(collectionName, cancellationToken);
        var prompt = capabilities.GeneratedFilterPromptKind switch
        {
            GeneratedFilterPromptKind.Milvus => prompts.FilterExpressionGeneratorPromptMilvus,
            GeneratedFilterPromptKind.Elasticsearch => prompts.FilterExpressionGeneratorPromptElasticsearch,
            _ => throw new NotSupportedException(
                $"Generated filters are not supported for prompt kind '{capabilities.GeneratedFilterPromptKind}'.")
        };
        var existingFilterContext = string.Empty;
        var values = new Dictionary<string, string?>
        {
            ["metadata_schema"] = string.IsNullOrWhiteSpace(schema) ? "No schema available." : schema,
            ["user_request"] = query,
            ["existing_filter_context"] = existingFilterContext
        };

        var request = new ChatCompletionRequest(
            Model: string.IsNullOrWhiteSpace(modelOverride)
                ? config.FilterExpressionGeneratorModelOrDefault
                : modelOverride,
            Messages:
            [
                new("system", PromptCatalog.Render(prompt.System, values)),
                new("user", PromptCatalog.Render(prompt.Human, values))
            ],
            Temperature: config.FilterExpressionGeneratorTemperature,
            TopP: config.FilterExpressionGeneratorTopP,
            MaxTokens: config.FilterExpressionGeneratorMaxTokens);

        using var activity = RagMetrics.ActivitySource.StartActivity("rag.Custom Metadata.token_usage");
        activity?.SetTag("rag.prompt.template", capabilities.GeneratedFilterPromptKind switch
        {
            GeneratedFilterPromptKind.Milvus => "filter_expression_generator_prompt_milvus",
            GeneratedFilterPromptKind.Elasticsearch => "filter_expression_generator_prompt_elasticsearch",
            _ => "filter_expression_generator_prompt"
        });
        activity?.SetTag("rag.prompt.message_count", request.Messages.Count);
        activity?.SetTag("rag.filter.collection_name", collectionName);
        activity?.SetTag("rag.filter.prompt_kind", capabilities.GeneratedFilterPromptKind.ToString());
        activity?.SetTag("gen_ai.request.model", request.Model);

        try
        {
            var response = await chatService.CompleteAsync(request, cancellationToken);
            RagTraceAttributes.SetLlmUsageTags(activity, response.Usage);
            var expr = response.Content?.Trim();

            if (string.IsNullOrEmpty(expr)
                || expr.Equals("None", StringComparison.OrdinalIgnoreCase)
                || expr.Equals("null", StringComparison.OrdinalIgnoreCase)
                || expr.Equals("NO_FILTER", StringComparison.OrdinalIgnoreCase)
                || expr.Equals("UNSUPPORTED", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            logger.LogInformation("Generated filter expression: {Expr}", expr);
            return expr;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogWarning(ex, "Filter expression generation failed; proceeding without filter.");
            return null;
        }
    }
}
