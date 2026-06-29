using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotnetRag.Rag.Clients;
using DotnetRag.Rag.Observability;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Models;
using DotnetRag.Shared.Prompts;
using DotnetRag.Shared.Summarization;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetRag.Rag.Services;

public sealed class RagService(
    RagServerConfiguration config,
    ILogger<RagService> logger,
    IChatCompletionService chatService,
    IChatCompletionClientFactory chatCompletionClientFactory,
    IVectorStore vectorStore,
    IVectorStoreManagement vectorStoreManagement,
    IVectorStoreClientFactory vectorStoreClientFactory,
    IRerankerClient rerankerClient,
    ISummarizationService summarizationService,
    QueryRewritingService queryRewritingService,
    QueryDecompositionService queryDecompositionService,
    ReflectionService reflectionService,
    FilterExpressionService filterExpressionService,
    IAgenticRagService agenticRagService,
    ICitationAssetResolver citationAssetResolver,
    IVlmContextAssembler vlmContextAssembler,
    RagMetrics metrics,
    PromptCatalog prompts,
    IServiceProvider serviceProvider)
{
    private static readonly Regex ThinkTokenRegex = new(
        @"<think>.*?</think>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Resolved once on first VLM call; null when VLM not configured
    private IChatCompletionService? VlmChatService =>
        serviceProvider.GetKeyedService<IChatCompletionService>("vlm");

    private sealed record RetrievalVectorStore(
        IVectorStore Store,
        IVectorStoreFilterCapabilities? FilterCapabilities);

    private sealed record RequestRoleServices(
        QueryRewritingService QueryRewriting,
        QueryDecompositionService QueryDecomposition,
        ReflectionService Reflection,
        FilterExpressionService FilterExpression,
        string? QueryRewriterModel,
        string? FilterExpressionGeneratorModel,
        string? ReflectionModel);

    private sealed class DisabledFilterCapabilities : IVectorStoreFilterCapabilities
    {
        public bool SupportsGeneratedFilters => false;
        public GeneratedFilterPromptKind GeneratedFilterPromptKind => GeneratedFilterPromptKind.None;

        public Task<string> GetFilterSchemaDescriptionAsync(
            string collectionName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
    }

    public async Task<RAGHealthResponse> HealthAsync(bool checkDependencies)
    {
        var databases = new List<DatabaseHealthInfo>();
        var objectStorage = new List<StorageHealthInfo>();
        var nim = new List<NIMServiceHealthInfo>();

        if (checkDependencies)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            // Vector store liveness check
            var vdbSw = Stopwatch.StartNew();
            var vdbStatus = await CheckVectorStoreHealthAsync(requestCancellationToken: default);
            vdbSw.Stop();
            databases.Add(new DatabaseHealthInfo(
                "vector_store", config.VectorStoreUrl, vdbStatus, vdbSw.Elapsed.TotalMilliseconds));

            // LLM endpoint liveness check
            var llmStatus = await PingAsync(http, GetLlmHealthUrl());
            nim.Add(new NIMServiceHealthInfo(
                "llm", config.LlmEndpoint, llmStatus, Model: config.LlmModel));

            // Embedding endpoint (only show separately when different from LLM)
            if (!string.Equals(config.EmbeddingEndpoint, config.LlmEndpoint,
                    StringComparison.OrdinalIgnoreCase))
            {
                var embedStatus = await PingAsync(http, GetEmbeddingHealthUrl());
                nim.Add(new NIMServiceHealthInfo(
                    "embedding", config.EmbeddingEndpoint, embedStatus,
                    Model: config.EmbeddingModel));
            }
        }

        return new RAGHealthResponse(
            Message: "Service is up.",
            Databases: databases,
            ObjectStorage: objectStorage,
            Nim: nim);
    }

    private static async Task<ServiceStatus> PingAsync(HttpClient http, string url)
    {
        try
        {
            var response = await http.GetAsync(url);
            return response.IsSuccessStatusCode ? ServiceStatus.Healthy : ServiceStatus.Unhealthy;
        }
        catch
        {
            return ServiceStatus.Unhealthy;
        }
    }

    // Ollama: GET /api/tags  — OpenAI-compatible NIM: GET /v1/models
    private string GetLlmHealthUrl()
    {
        var base_ = config.LlmEndpoint.TrimEnd('/');
        return config.LlmProvider.Equals("ollama", StringComparison.OrdinalIgnoreCase)
            ? $"{base_}/api/tags"
            : $"{base_}/v1/models";
    }

    private string GetEmbeddingHealthUrl()
    {
        var base_ = config.EmbeddingEndpoint.TrimEnd('/');
        return config.EmbeddingProvider.Equals("ollama", StringComparison.OrdinalIgnoreCase)
            ? $"{base_}/api/tags"
            : $"{base_}/v1/models";
    }

    private async Task<ServiceStatus> CheckVectorStoreHealthAsync(
        CancellationToken requestCancellationToken)
    {
        try
        {
            return await vectorStoreManagement.CheckHealthAsync(requestCancellationToken)
                ? ServiceStatus.Healthy
                : ServiceStatus.Unhealthy;
        }
        catch
        {
            return ServiceStatus.Unhealthy;
        }
    }

    public ConfigurationResponse GetConfiguration()
    {
        return new ConfigurationResponse(
            RagConfiguration: new RagConfigurationDefaults(
                Temperature: config.Temperature,
                TopP: config.TopP,
                MaxTokens: config.MaxTokens,
                VdbTopK: config.VdbTopK,
                RerankerTopK: config.RerankerTopK,
                ConfidenceThreshold: config.ConfidenceThreshold),
            FeatureToggles: new FeatureTogglesDefaults(
                EnableReranker: config.EnableReranker,
                EnableCitations: config.EnableCitations,
                EnableGuardrails: config.EnableGuardrails,
                EnableQueryRewriting: config.EnableQueryRewriting,
                EnableQueryDecomposition: config.EnableQueryDecomposition,
                EnableVlmInference: config.EnableVlmInference,
                EnableFilterGenerator: config.EnableFilterGenerator,
                EnableAgenticRag: config.EnableAgenticRag),
            Models: new ModelsDefaults(
                LlmModel: config.LlmModel,
                EmbeddingModel: config.EmbeddingModel,
                RerankerModel: config.RerankerModel,
                VlmModel: config.VlmModel,
                QueryRewriterModel: config.QueryRewriterModelOrDefault,
                FilterExpressionGeneratorModel: config.FilterExpressionGeneratorModelOrDefault,
                ReflectionModel: config.ReflectionModelOrDefault),
            Endpoints: new EndpointsDefaults(
                LlmEndpoint: config.LlmEndpoint,
                EmbeddingEndpoint: config.EmbeddingEndpoint,
                RerankerEndpoint: config.RerankerServiceUrl,
                VlmEndpoint: config.VlmEndpoint,
                VdbEndpoint: config.VectorStoreUrl,
                QueryRewriterEndpoint: config.QueryRewriterEndpointOrDefault,
                FilterExpressionGeneratorEndpoint: config.FilterExpressionGeneratorEndpointOrDefault,
                ReflectionEndpoint: config.ReflectionEndpointOrDefault),
            Providers: new ProvidersDefaults(
                LlmProvider: config.LlmProvider,
                EmbeddingProvider: config.EmbeddingProvider,
                VlmProvider: config.VlmProvider,
                VectorStoreProvider: config.VectorStoreName));
    }

    public async Task<IResult> GenerateAsync(HttpRequest request, Prompt prompt)
    {
        using var activity = RagMetrics.ActivitySource.StartActivity("rag.generate");
        var sw = Stopwatch.StartNew();
        metrics.GenerateRequests.Add(1);
        using var _ = metrics.TrackActiveRequest();

        try
        {
            return await GenerateInternalAsync(request, prompt);
        }
        catch
        {
            metrics.GenerateErrors.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            metrics.GenerateLatency.Record(sw.Elapsed.TotalSeconds);
        }
    }

    private async Task<IResult> GenerateInternalAsync(HttpRequest request, Prompt prompt)
    {
        if (agenticRagService.IsRequested(prompt))
        {
            return await agenticRagService.GenerateAsync(
                AgenticRagInvocation.From(request, prompt),
                request.HttpContext.RequestAborted);
        }

        if (ValidateTopKAndConfidence(
            prompt.VdbTopK,
            prompt.RerankerTopK,
            prompt.ConfidenceThreshold,
            "generate") is { } validationError)
        {
            return validationError;
        }

        var allMessages = prompt.Messages.ToList();

        // Apply conversation history window (0 = unlimited)
        var windowedMessages = config.ConversationHistory > 0
            ? allMessages.TakeLast(config.ConversationHistory).ToList()
            : allMessages;

        // VLM routing: when VLM is enabled and messages contain image content,
        // route to the multimodal endpoint and skip knowledge base retrieval.
        var useVlm = (prompt.EnableVlmInference || config.EnableVlmInference)
            && HasImageContent(allMessages)
            && (VlmChatService is not null || !string.IsNullOrWhiteSpace(prompt.VlmEndpoint));

        if (useVlm)
        {
            return await HandleVlmRequestAsync(request, prompt, windowedMessages);
        }

        List<ChatMessage> chatMessages;
        List<VectorSearchResult> contextChunks = [];
        var roleServices = ResolveRequestRoleServices(
            prompt.QueryRewriterEndpoint,
            prompt.QueryRewriterModel,
            prompt.QueryRewriterApiKey,
            prompt.FilterExpressionGeneratorEndpoint,
            prompt.FilterExpressionGeneratorModel,
            prompt.FilterExpressionGeneratorApiKey,
            prompt.ReflectionEndpoint,
            prompt.ReflectionModel,
            prompt.ReflectionApiKey);

        if (prompt.UseKnowledgeBase)
        {
            var retrievalVectorStore = ResolveRequestVectorStore(
                prompt.VdbEndpoint,
                prompt.EmbeddingEndpoint,
                prompt.EmbeddingModel,
                request);
            (chatMessages, contextChunks) = await BuildRagMessagesAsync(
                prompt,
                windowedMessages,
                retrievalVectorStore,
                roleServices,
                request.HttpContext.RequestAborted);
        }
        else
        {
            chatMessages = new List<ChatMessage>
            {
                new("system", prompts.ChatTemplate.System)
            };
            chatMessages.AddRange(windowedMessages
                .Select(m => new ChatMessage(m.Role, ExtractTextContent(m.Content)))
                .ToList());
        }

        var enableThinking = prompt.MinThinkingTokens.GetValueOrDefault() > 0
            || prompt.MaxThinkingTokens.GetValueOrDefault() > 0;
        var chatRequest = new ChatCompletionRequest(
            Model: prompt.Model ?? config.LlmModel,
            Messages: chatMessages,
            EnableThinking: enableThinking,
            MaxTokens: prompt.MaxTokens ?? config.MaxTokens,
            Temperature: prompt.Temperature ?? config.Temperature,
            TopP: prompt.TopP ?? config.TopP,
            ThinkingTokenBudget: prompt.MaxThinkingTokens);
        SetGenerateSpanRequestTags(
            Activity.Current,
            request,
            prompt,
            chatRequest.Model,
            prompt.UseKnowledgeBase ? "rag_template" : "chat_template",
            contextChunks.Count);
        var activeChatService = ResolveRequestChatService(
            prompt.LlmEndpoint,
            prompt.Model ?? config.LlmModel,
            config.LlmProvider,
            chatService);

        // /chat/completions → non-streaming JSON (OpenAI-compatible clients)
        if (request.Path.Value?.Contains("chat/completions", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                var chatResponse = await activeChatService.CompleteAsync(chatRequest, request.HttpContext.RequestAborted);
                var content = config.FilterThinkTokens
                    ? ThinkTokenRegex.Replace(chatResponse.Content, string.Empty).Trim()
                    : chatResponse.Content;
                RagTraceAttributes.SetLlmUsageTags(Activity.Current, chatResponse.Usage);

                // Guardrails: clear citations if the model rejected the request
                var citations = contextChunks;
                if (prompt.EnableGuardrails && IsGuardrailRejection(content))
                {
                    logger.LogInformation("Guardrail triggered; clearing citations.");
                    citations = [];
                }

                // Reflection — response groundedness
                if (config.EnableReflection && !IsGuardrailRejection(content))
                {
                    var contextText = BuildContextString(citations, config.EnableSourceMetadata);
                    var userQuery = ExtractTextContent(chatMessages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty);
                    var (_, improved) = await roleServices.Reflection.CheckResponseGroundednessAsync(
                        userQuery,
                        contextText,
                        content,
                        request.HttpContext.RequestAborted,
                        roleServices.ReflectionModel);
                    if (improved is not null) content = improved;
                }

                var citationsPayload = prompt.UseKnowledgeBase
                    ? await BuildCitationsPayloadAsync(
                        prompt.EnableCitations ? citations : [],
                        request.HttpContext.RequestAborted)
                    : null;

                return Results.Json(BuildOpenAiCompatibleResponse(
                    chatRequest.Model,
                    content,
                    chatResponse.Usage,
                    citationsPayload));
            }
            catch (HttpRequestException ex)
            {
                logger.LogError(ex, "Failed to invoke LLM");
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        }

        // /generate → SSE streaming (what the React frontend expects)
        await WriteGenerateSseAsync(
            request.HttpContext,
            prompt,
            chatRequest,
            contextChunks,
            activeChatService,
            roleServices.Reflection,
            roleServices.ReflectionModel);
        return Results.Empty;
    }

    // Streams the LLM response as SSE in the format the frontend's processStream() expects:
    //   data: {"choices":[{"delta":{"content":"token"},"finish_reason":null}]}\n\n
    //   data: {"choices":[{"delta":{"content":""},"finish_reason":"stop"}],"citations":{...}}\n\n
    //
    // When reflection or guardrails are active the response is buffered, checked,
    // and potentially replaced before any SSE bytes are sent to the client.
    private async Task WriteGenerateSseAsync(
        HttpContext ctx,
        Prompt prompt,
        ChatCompletionRequest chatRequest,
        List<VectorSearchResult> contextChunks,
        IChatCompletionService activeChatService,
        ReflectionService activeReflectionService,
        string? reflectionModelOverride)
    {
        var ct = ctx.RequestAborted;

        if (!prompt.EnableGuardrails && !config.EnableReflection)
        {
            if (config.FilterThinkTokens)
            {
                await WriteFilteredGenerateSseAsync(ctx, prompt, chatRequest, contextChunks, activeChatService);
            }
            else
            {
                await WriteDirectGenerateSseAsync(ctx, prompt, chatRequest, contextChunks, activeChatService);
            }
            return;
        }

        // Collect all tokens — needed for guardrail/groundedness checks.
        // For most requests this is a fast in-memory accumulation.
        var tokenBuffer = new System.Text.StringBuilder();
        var reasoningBuffer = new System.Text.StringBuilder();
        IReadOnlyDictionary<string, object?>? streamUsage = null;
        try
        {
            await foreach (var delta in activeChatService.StreamDeltasAsync(chatRequest, ct))
            {
                tokenBuffer.Append(delta.Content);
                reasoningBuffer.Append(delta.ReasoningContent);
                streamUsage = delta.Usage ?? streamUsage;
            }
        }
        catch (OperationCanceledException) { return; }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "LLM stream failed");
        }

        var fullContent = config.FilterThinkTokens
            ? ThinkTokenRegex.Replace(tokenBuffer.ToString(), string.Empty).Trim()
            : tokenBuffer.ToString();

        var activeChunks = contextChunks;

        // Guardrails: if model signals rejection, clear citations
        if (prompt.EnableGuardrails && IsGuardrailRejection(fullContent))
        {
            logger.LogInformation("Guardrail triggered on SSE path; clearing citations.");
            activeChunks = [];
        }

        // Reflection — response groundedness
        if (config.EnableReflection && !IsGuardrailRejection(fullContent) && activeChunks.Count > 0)
        {
            var contextText = BuildContextString(activeChunks, config.EnableSourceMetadata);
            var userQuery = ExtractTextContent(chatRequest.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty);
            var (_, improved) = await activeReflectionService.CheckResponseGroundednessAsync(
                userQuery,
                contextText,
                fullContent,
                ct,
                reflectionModelOverride);
            if (improved is not null) fullContent = improved;
        }

        // Now stream the (possibly improved) content token by token
        var resp = ctx.Response;
        resp.ContentType = "text/event-stream; charset=utf-8";
        resp.Headers["Cache-Control"] = "no-cache";
        resp.Headers["X-Accel-Buffering"] = "no";

        if (reasoningBuffer.Length > 0)
        {
            await WriteSseDeltaAsync(
                resp,
                content: null,
                reasoningContent: reasoningBuffer.ToString(),
                CancellationToken.None);
        }

        // Emit content in reasonably-sized chunks so the frontend can render progressively
        const int chunkSize = 20;
        for (int i = 0; i < fullContent.Length; i += chunkSize)
        {
            var slice = fullContent.Substring(i, Math.Min(chunkSize, fullContent.Length - i));
            await WriteSseDeltaAsync(resp, slice, reasoningContent: null, CancellationToken.None);
        }

        // Final event: finish_reason=stop + citations
        await WriteSseFinalAsync(
            resp,
            prompt.EnableCitations ? activeChunks : [],
            streamUsage,
            CancellationToken.None);
    }

    private async Task WriteDirectGenerateSseAsync(
        HttpContext ctx,
        Prompt prompt,
        ChatCompletionRequest chatRequest,
        List<VectorSearchResult> contextChunks,
        IChatCompletionService activeChatService)
    {
        var resp = ctx.Response;
        resp.ContentType = "text/event-stream; charset=utf-8";
        resp.Headers["Cache-Control"] = "no-cache";
        resp.Headers["X-Accel-Buffering"] = "no";
        IReadOnlyDictionary<string, object?>? streamUsage = null;

        try
        {
            await foreach (var delta in activeChatService.StreamDeltasAsync(chatRequest, ctx.RequestAborted))
            {
                streamUsage = delta.Usage ?? streamUsage;
                if (!string.IsNullOrEmpty(delta.Content) || !string.IsNullOrEmpty(delta.ReasoningContent))
                {
                    await WriteSseDeltaAsync(
                        resp,
                        delta.Content,
                        delta.ReasoningContent,
                        CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "LLM stream failed");
        }

        await WriteSseFinalAsync(
            resp,
            prompt.EnableCitations ? contextChunks : [],
            streamUsage,
            CancellationToken.None);
    }

    private async Task WriteFilteredGenerateSseAsync(
        HttpContext ctx,
        Prompt prompt,
        ChatCompletionRequest chatRequest,
        List<VectorSearchResult> contextChunks,
        IChatCompletionService activeChatService)
    {
        var resp = ctx.Response;
        resp.ContentType = "text/event-stream; charset=utf-8";
        resp.Headers["Cache-Control"] = "no-cache";
        resp.Headers["X-Accel-Buffering"] = "no";
        IReadOnlyDictionary<string, object?>? streamUsage = null;
        var thinkFilter = new ThinkTokenStreamFilter();

        try
        {
            await foreach (var delta in activeChatService.StreamDeltasAsync(chatRequest, ctx.RequestAborted))
            {
                streamUsage = delta.Usage ?? streamUsage;
                if (!string.IsNullOrEmpty(delta.ReasoningContent))
                {
                    await WriteSseDeltaAsync(
                        resp,
                        content: null,
                        reasoningContent: delta.ReasoningContent,
                        CancellationToken.None);
                }

                if (!string.IsNullOrEmpty(delta.Content))
                {
                    var visibleContent = thinkFilter.Process(delta.Content);
                    if (!string.IsNullOrEmpty(visibleContent))
                    {
                        await WriteSseDeltaAsync(
                            resp,
                            visibleContent,
                            reasoningContent: null,
                            CancellationToken.None);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "LLM stream failed");
        }

        var finalVisibleContent = thinkFilter.Flush();
        if (!string.IsNullOrEmpty(finalVisibleContent))
        {
            await WriteSseDeltaAsync(
                resp,
                finalVisibleContent,
                reasoningContent: null,
                CancellationToken.None);
        }

        await WriteSseFinalAsync(
            resp,
            prompt.EnableCitations ? contextChunks : [],
            streamUsage,
            CancellationToken.None);
    }

    private sealed class ThinkTokenStreamFilter
    {
        private const string OpenTag = "<think>";
        private const string CloseTag = "</think>";
        private readonly System.Text.StringBuilder _buffer = new();
        private bool _insideThink;

        public string Process(string content)
        {
            _buffer.Append(content);
            var output = new System.Text.StringBuilder();

            while (_buffer.Length > 0)
            {
                if (_insideThink)
                {
                    var closeIndex = IndexOfOrdinalIgnoreCase(_buffer, CloseTag);
                    if (closeIndex < 0)
                    {
                        KeepPossibleTagSuffix(CloseTag.Length - 1);
                        break;
                    }

                    _buffer.Remove(0, closeIndex + CloseTag.Length);
                    _insideThink = false;
                    continue;
                }

                var openIndex = IndexOfOrdinalIgnoreCase(_buffer, OpenTag);
                if (openIndex < 0)
                {
                    var emitLength = Math.Max(0, _buffer.Length - (OpenTag.Length - 1));
                    if (emitLength == 0)
                    {
                        break;
                    }

                    output.Append(_buffer.ToString(0, emitLength));
                    _buffer.Remove(0, emitLength);
                    break;
                }

                if (openIndex > 0)
                {
                    output.Append(_buffer.ToString(0, openIndex));
                }

                _buffer.Remove(0, openIndex + OpenTag.Length);
                _insideThink = true;
            }

            return output.ToString();
        }

        public string Flush()
        {
            if (_insideThink)
            {
                _buffer.Clear();
                return string.Empty;
            }

            var remaining = _buffer.ToString();
            _buffer.Clear();
            return remaining;
        }

        private void KeepPossibleTagSuffix(int maxSuffixLength)
        {
            if (_buffer.Length <= maxSuffixLength)
            {
                return;
            }

            _buffer.Remove(0, _buffer.Length - maxSuffixLength);
        }

        private static int IndexOfOrdinalIgnoreCase(System.Text.StringBuilder value, string search)
        {
            var text = value.ToString();
            return text.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsGuardrailRejection(string content) =>
        content.Contains("I'm sorry, I can't respond to that.", StringComparison.OrdinalIgnoreCase);

    // ── VLM helpers ───────────────────────────────────────────────────────────

    private static bool HasImageContent(IReadOnlyList<Message> messages)
    {
        foreach (var msg in messages)
        {
            if (msg.Content is JsonElement el && el.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in el.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var t) && t.GetString() == "image_url")
                        return true;
                }
            }
        }
        return false;
    }

    // Converts prompt messages to VLM-compatible ChatMessages preserving image parts
    // and capping the total image count at maxTotalImages.
    private static List<ChatMessage> BuildVlmMessages(
        IReadOnlyList<Message> messages,
        int maxTotalImages)
    {
        var imageCount = 0;
        var result = new List<ChatMessage>();

        foreach (var msg in messages)
        {
            if (msg.Content is JsonElement el && el.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<object>();
                foreach (var part in el.EnumerateArray())
                {
                    if (!part.TryGetProperty("type", out var typeEl)) continue;
                    var type = typeEl.GetString();
                    if (type == "image_url")
                    {
                        if (imageCount >= maxTotalImages) continue;
                        imageCount++;
                    }
                    // Clone the JsonElement as a raw JSON string to avoid disposal issues
                    parts.Add(JsonSerializer.Deserialize<object>(part.GetRawText())!);
                }
                result.Add(new ChatMessage(msg.Role, parts));
            }
            else
            {
                result.Add(new ChatMessage(msg.Role, ExtractTextContent(msg.Content)));
            }
        }

        return result;
    }

    // Handles a generate request that should be routed to the VLM endpoint.
    // Falls back to the main LLM if VLM fails and VlmToLlmFallback is configured.
    private async Task<IResult> HandleVlmRequestAsync(
        HttpRequest request,
        Prompt prompt,
        IReadOnlyList<Message> windowedMessages)
    {
        var maxImages = prompt.VlmMaxTotalImages ?? config.VlmMaxTotalImages;
        List<VectorSearchResult> contextChunks = [];
        List<VlmContextAsset> contextAssets = [];
        if (prompt.UseKnowledgeBase)
        {
            var roleServices = ResolveRequestRoleServices(
                prompt.QueryRewriterEndpoint,
                prompt.QueryRewriterModel,
                prompt.QueryRewriterApiKey,
                prompt.FilterExpressionGeneratorEndpoint,
                prompt.FilterExpressionGeneratorModel,
                prompt.FilterExpressionGeneratorApiKey,
                prompt.ReflectionEndpoint,
                prompt.ReflectionModel,
                prompt.ReflectionApiKey);
            var retrievalVectorStore = ResolveRequestVectorStore(
                prompt.VdbEndpoint,
                prompt.EmbeddingEndpoint,
                prompt.EmbeddingModel,
                request);
            (_, contextChunks) = await BuildRagMessagesAsync(
                prompt,
                windowedMessages,
                retrievalVectorStore,
                roleServices,
                request.HttpContext.RequestAborted);
            contextAssets = await BuildVlmContextAssetsAsync(
                contextChunks,
                request.HttpContext.RequestAborted);
        }

        var vlmMessages = vlmContextAssembler.Assemble(new VlmContextAssemblyRequest(
            windowedMessages,
            contextChunks,
            contextAssets,
            prompts.VlmTemplate.System,
            prompts.VlmTemplate.Human,
            maxImages,
            config.EnableSourceMetadata));

        var vlmModel = prompt.VlmModel ?? config.VlmModel;
        var vlmRequest = new ChatCompletionRequest(
            Model: vlmModel,
            Messages: vlmMessages,
            EnableThinking: prompt.VlmEnableThinking ?? false,
            MaxTokens: prompt.VlmMaxTokens ?? prompt.MaxTokens ?? config.MaxTokens,
            Temperature: prompt.VlmTemperature ?? prompt.Temperature ?? config.Temperature,
            TopP: prompt.VlmTopP ?? prompt.TopP ?? config.TopP,
            ThinkingTokenBudget: prompt.VlmThinkingTokenBudget);
        SetGenerateSpanRequestTags(
            Activity.Current,
            request,
            prompt,
            vlmModel,
            "vlm_template",
            contextChunks.Count);

        var activeVlmService = ResolveRequestVlmService(prompt, vlmModel);
        var fallbackChatService = ResolveRequestChatService(
            prompt.LlmEndpoint,
            prompt.Model ?? config.LlmModel,
            config.LlmProvider,
            chatService);

        // SSE path
        if (request.Path.Value?.Contains("chat/completions", StringComparison.OrdinalIgnoreCase) != true)
        {
            var ct = request.HttpContext.RequestAborted;
            var tokenBuffer = new StringBuilder();
            var reasoningBuffer = new StringBuilder();
            IReadOnlyDictionary<string, object?>? streamUsage = null;
            var usedFallback = false;

            try
            {
                await foreach (var delta in activeVlmService.StreamDeltasAsync(vlmRequest, ct))
                {
                    tokenBuffer.Append(delta.Content);
                    reasoningBuffer.Append(delta.ReasoningContent);
                    streamUsage = delta.Usage ?? streamUsage;
                }
            }
            catch (Exception ex) when (config.VlmToLlmFallback)
            {
                logger.LogWarning(ex, "VLM stream failed; falling back to main LLM.");
                usedFallback = true;
                tokenBuffer.Clear();
                reasoningBuffer.Clear();
                streamUsage = null;
                var fallbackRequest = vlmRequest with { Model = config.LlmModel };
                await foreach (var delta in fallbackChatService.StreamDeltasAsync(fallbackRequest, ct))
                {
                    tokenBuffer.Append(delta.Content);
                    reasoningBuffer.Append(delta.ReasoningContent);
                    streamUsage = delta.Usage ?? streamUsage;
                }
            }

            var content = config.FilterThinkTokens
                ? ThinkTokenRegex.Replace(tokenBuffer.ToString(), string.Empty).Trim()
                : tokenBuffer.ToString();

            if (usedFallback)
                logger.LogInformation("VLM fallback to LLM succeeded.");

            var resp = request.HttpContext.Response;
            resp.ContentType = "text/event-stream; charset=utf-8";
            resp.Headers["Cache-Control"] = "no-cache";
            resp.Headers["X-Accel-Buffering"] = "no";

            const int chunkSize = 20;
            if (reasoningBuffer.Length > 0)
            {
                await WriteSseDeltaAsync(
                    resp,
                    content: null,
                    reasoningContent: reasoningBuffer.ToString(),
                    CancellationToken.None);
            }

            for (int i = 0; i < content.Length; i += chunkSize)
            {
                var slice = content.Substring(i, Math.Min(chunkSize, content.Length - i));
                await WriteSseDeltaAsync(resp, slice, reasoningContent: null, CancellationToken.None);
            }

            await WriteSseFinalAsync(resp, [], streamUsage, CancellationToken.None);
            return Results.Empty;
        }

        // Non-streaming path
        try
        {
            var chatResponse = await activeVlmService.CompleteAsync(vlmRequest, request.HttpContext.RequestAborted);
            var content = config.FilterThinkTokens
                ? ThinkTokenRegex.Replace(chatResponse.Content, string.Empty).Trim()
                : chatResponse.Content;
            RagTraceAttributes.SetLlmUsageTags(Activity.Current, chatResponse.Usage);
            return Results.Json(BuildOpenAiCompatibleResponse(vlmModel, content, chatResponse.Usage));
        }
        catch (Exception ex) when (config.VlmToLlmFallback)
        {
            logger.LogWarning(ex, "VLM call failed; falling back to main LLM.");
            var fallbackRequest = vlmRequest with { Model = config.LlmModel };
            var chatResponse = await fallbackChatService.CompleteAsync(fallbackRequest, request.HttpContext.RequestAborted);
            var content = config.FilterThinkTokens
                ? ThinkTokenRegex.Replace(chatResponse.Content, string.Empty).Trim()
                : chatResponse.Content;
            RagTraceAttributes.SetLlmUsageTags(Activity.Current, chatResponse.Usage);
            return Results.Json(BuildOpenAiCompatibleResponse(config.LlmModel, content, chatResponse.Usage));
        }
    }

    private async Task<List<VlmContextAsset>> BuildVlmContextAssetsAsync(
        IReadOnlyList<VectorSearchResult> contextChunks,
        CancellationToken cancellationToken)
    {
        var assets = new List<VlmContextAsset>();
        foreach (var chunk in contextChunks)
        {
            var asset = await citationAssetResolver.ResolveAsync(chunk, cancellationToken);
            if (asset is null)
            {
                continue;
            }

            assets.Add(new VlmContextAsset(
                asset.ContentBase64,
                asset.DocumentType,
                chunk.Metadata?.GetValueOrDefault("filename")
                    ?? chunk.Metadata?.GetValueOrDefault("source")
                    ?? chunk.Id,
                GetMetadataInt(chunk.Metadata, "page_number", "content_metadata.page_number"),
                chunk.Text));
        }

        return assets;
    }

    public async Task<IResult> SearchAsync(HttpRequest request, DocumentSearch data)
    {
        using var activity = RagMetrics.ActivitySource.StartActivity("rag.search");
        var sw = Stopwatch.StartNew();
        metrics.SearchRequests.Add(1);

        try
        {
            return await SearchInternalAsync(request, data);
        }
        catch
        {
            metrics.SearchErrors.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            metrics.SearchLatency.Record(sw.Elapsed.TotalSeconds);
        }
    }

    private async Task<IResult> SearchInternalAsync(HttpRequest request, DocumentSearch data)
    {
        if (ValidateTopKAndConfidence(
            data.VdbTopK,
            data.RerankerTopK,
            data.ConfidenceThreshold,
            "search") is { } validationError)
        {
            return validationError;
        }

        var roleServices = ResolveRequestRoleServices(
            data.QueryRewriterEndpoint,
            data.QueryRewriterModel,
            data.QueryRewriterApiKey,
            data.FilterExpressionGeneratorEndpoint,
            data.FilterExpressionGeneratorModel,
            data.FilterExpressionGeneratorApiKey,
            null,
            null,
            null);
        var rawQuery = data.Query is string s ? s : JsonSerializer.Serialize(data.Query);
        var queryText = data.EnableQueryRewriting && config.ConversationHistory > 0 && data.Messages?.Count > 0
            ? await roleServices.QueryRewriting.RewriteAsync(
                rawQuery,
                data.Messages,
                request.HttpContext.RequestAborted,
                roleServices.QueryRewriterModel)
            : rawQuery;

        var collectionNames = ResolveCollectionNames(data.CollectionNames);
        var topK = data.VdbTopK > 0 ? data.VdbTopK : config.VdbTopK;
        var threshold = data.ConfidenceThreshold ?? config.ConfidenceThreshold;
        var retrievalVectorStore = ResolveRequestVectorStore(
            data.VdbEndpoint,
            data.EmbeddingEndpoint,
            data.EmbeddingModel,
            request);

        try
        {
            var filterMap = await BuildCollectionFilterMapAsync(
                queryText,
                collectionNames,
                data.FilterExpr?.ToString(),
                data.EnableFilterGenerator || config.EnableFilterGenerator,
                retrievalVectorStore.FilterCapabilities,
                roleServices.FilterExpression,
                roleServices.FilterExpressionGeneratorModel,
                request.HttpContext.RequestAborted);
            var rawResults = await SearchAcrossCollectionsAsync(
                retrievalVectorStore.Store,
                collectionNames,
                queryText,
                topK,
                filterMap,
                request.HttpContext.RequestAborted);
            var shouldRerank = (data.EnableReranker && config.EnableReranker);
            var reranked = await ApplyRerankingAsync(
                queryText,
                rawResults.ToList(),
                data.RerankerTopK > 0 ? data.RerankerTopK : config.RerankerTopK,
                shouldRerank,
                request.HttpContext.RequestAborted,
                data.RerankerEndpoint,
                threshold);

            var filtered = reranked
                .Select(r => ToVectorStoreSearchResultItem(r))
                .ToList();

            logger.LogInformation(
                "Search returned {Count} results from {CollectionCount} collection(s)",
                filtered.Count,
                collectionNames.Count);

            return Results.Ok(new Citations(Results: filtered, Message: "Search completed."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Vector store search failed.");
            return Results.Json(
                new { message = $"Search failed: {ex.Message}" },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    public async Task<IResult> VectorStoreSearchAsync(
        HttpRequest request,
        string vectorStoreId,
        VectorStoreSearchRequest searchRequest)
    {
        if (ValidateVectorStoreSearchRequest(searchRequest) is { } validationError)
        {
            return validationError;
        }

        var roleServices = ResolveRequestRoleServices(
            searchRequest.QueryRewriterEndpoint,
            searchRequest.QueryRewriterModel,
            searchRequest.QueryRewriterApiKey,
            searchRequest.FilterExpressionGeneratorEndpoint,
            searchRequest.FilterExpressionGeneratorModel,
            searchRequest.FilterExpressionGeneratorApiKey,
            null,
            null,
            null);
        var rawQueryText = searchRequest.Query is string s ? s : JsonSerializer.Serialize(searchRequest.Query);
        var queryText = searchRequest.RewriteQuery
            ? await roleServices.QueryRewriting.RewriteAsync(
                rawQueryText,
                [new Message("user", rawQueryText)],
                request.HttpContext.RequestAborted,
                roleServices.QueryRewriterModel)
            : rawQueryText;
        var topK = searchRequest.MaxNumResults > 0 ? searchRequest.MaxNumResults : config.VdbTopK;
        var threshold = searchRequest.RankingOptions?.ScoreThreshold > 0
            ? searchRequest.RankingOptions.ScoreThreshold
            : 0.0;
        var retrievalVectorStore = ResolveRequestVectorStore(
            searchRequest.VdbEndpoint,
            searchRequest.EmbeddingEndpoint,
            searchRequest.EmbeddingModel,
            request);
        var filterExpr = OpenAiFilterToExpression(searchRequest.Filters);

        try
        {
            var rawResults = await retrievalVectorStore.Store.SearchAsync(
                vectorStoreId,
                queryText,
                topK,
                filterExpr,
                request.HttpContext.RequestAborted);
            var shouldRerank = ShouldRerankVectorStoreSearch(searchRequest.RankingOptions);
            var reranked = await ApplyRerankingAsync(
                queryText,
                rawResults.ToList(),
                topK,
                shouldRerank,
                request.HttpContext.RequestAborted,
                searchRequest.RerankerEndpoint,
                threshold);

            var items = reranked
                .Select(r => ToVectorStoreSearchResultItem(r, vectorStoreId))
                .ToList();

            return Results.Ok(new VectorStoreSearchResponse(
                Object: "vector_store.search_results.page",
                SearchQuery: queryText,
                Data: items));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "VectorStore search failed for '{VectorStoreId}'", vectorStoreId);
            return Results.Json(
                new { message = $"Search failed: {ex.Message}" },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static IResult? ValidateTopKAndConfidence(
        int vdbTopK,
        int rerankerTopK,
        double? confidenceThreshold,
        string requestName)
    {
        if (vdbTopK <= 0)
        {
            return Results.BadRequest(new
            {
                message = $"vdb_top_k must be greater than 0 for {requestName}, got {vdbTopK}."
            });
        }

        if (rerankerTopK <= 0)
        {
            return Results.BadRequest(new
            {
                message = $"reranker_top_k must be greater than 0 for {requestName}, got {rerankerTopK}."
            });
        }

        if (rerankerTopK > vdbTopK)
        {
            return Results.BadRequest(new
            {
                message = $"reranker_top_k({rerankerTopK}) must be less than or equal to vdb_top_k ({vdbTopK}). Please check your settings and try again."
            });
        }

        return ValidateConfidenceThreshold(confidenceThreshold);
    }

    private static IResult? ValidateVectorStoreSearchRequest(VectorStoreSearchRequest searchRequest)
    {
        if (searchRequest.MaxNumResults <= 0)
        {
            return Results.BadRequest(new
            {
                message = $"max_num_results must be greater than 0, got {searchRequest.MaxNumResults}."
            });
        }

        if (searchRequest.RankingOptions?.ScoreThreshold is { } scoreThreshold
            && ValidateConfidenceThreshold(scoreThreshold) is { } thresholdError)
        {
            return thresholdError;
        }

        if (searchRequest.RankingOptions?.Ranker is { } ranker
            && !IsRecognizedRanker(ranker))
        {
            return Results.BadRequest(new
            {
                message = $"ranking_options.ranker must be one of auto, true, on, enabled, none, false, off, or disabled, got '{ranker}'."
            });
        }

        return null;
    }

    private static IResult? ValidateConfidenceThreshold(double? confidenceThreshold)
    {
        if (!confidenceThreshold.HasValue)
        {
            return null;
        }

        var value = confidenceThreshold.Value;
        if (value < 0.0)
        {
            return Results.BadRequest(new
            {
                message = $"confidence_threshold must be >= 0.0, got {value}. The confidence threshold represents the minimum relevance score required for documents to be included."
            });
        }

        if (value > 1.0)
        {
            return Results.BadRequest(new
            {
                message = $"confidence_threshold must be <= 1.0, got {value}. The confidence threshold represents the minimum relevance score required for documents to be included. Values range from 0.0 (no filtering) to 1.0 (only perfect matches)."
            });
        }

        return null;
    }

    private bool ShouldRerankVectorStoreSearch(RankingOptions? rankingOptions)
    {
        if (rankingOptions?.Ranker is not { } ranker)
        {
            return config.EnableReranker;
        }

        return ranker.Trim().ToLowerInvariant() switch
        {
            "none" or "false" or "off" or "disabled" => false,
            "auto" or "true" or "on" or "enabled" => config.EnableReranker,
            _ => config.EnableReranker
        };
    }

    private static bool IsRecognizedRanker(string ranker)
        => ranker.Trim().ToLowerInvariant() is "none" or "false" or "off" or "disabled"
            or "auto" or "true" or "on" or "enabled";

    private static string? OpenAiFilterToExpression(OpenAiFilter? filter)
    {
        return filter switch
        {
            null => null,
            ComparisonFilter comparison => ComparisonFilterToExpression(comparison),
            CompoundFilter compound => CompoundFilterToExpression(compound),
            _ => null
        };
    }

    private static string? CompoundFilterToExpression(CompoundFilter compound)
    {
        var op = compound.Type.Trim().ToLowerInvariant() switch
        {
            "and" => "AND",
            "or" => "OR",
            _ => null
        };
        if (op is null)
        {
            return null;
        }

        var parts = compound.Filters
            .Select(OpenAiFilterToExpression)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => $"({part})")
            .ToList();

        return parts.Count == 0 ? null : string.Join($" {op} ", parts);
    }

    private static string? ComparisonFilterToExpression(ComparisonFilter comparison)
    {
        var op = comparison.Type.Trim().ToLowerInvariant() switch
        {
            "eq" or "==" => "==",
            "ne" or "!=" => "!=",
            "gt" or ">" => ">",
            "gte" or ">=" => ">=",
            "lt" or "<" => "<",
            "lte" or "<=" => "<=",
            _ => null
        };
        if (op is null)
        {
            return null;
        }

        return $"{NormalizeFilterKey(comparison.Key)} {op} {FormatFilterValue(comparison.Value)}";
    }

    private static string NormalizeFilterKey(string key)
    {
        var trimmed = key.Trim();
        if (trimmed.StartsWith("content_metadata[", StringComparison.Ordinal)
            || trimmed.StartsWith("metadata[", StringComparison.Ordinal)
            || trimmed.Contains('.', StringComparison.Ordinal))
        {
            return trimmed;
        }

        return $"content_metadata[\"{trimmed}\"]";
    }

    private static string FormatFilterValue(object value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => element.GetRawText()
            };
        }

        return value switch
        {
            string text => JsonSerializer.Serialize(text),
            bool boolean => boolean ? "true" : "false",
            int or long or float or double or decimal => Convert.ToString(
                value,
                System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            _ => JsonSerializer.Serialize(value)
        };
    }

    // ORIG: nvidia_rag/rag_server/main.py::get_summary
    // Reads from provider-selected vector summary collection "summary_{collectionName}".
    // When blocking=true, polls until the summary is ready or the timeout elapses.
    public async Task<IResult> GetSummaryAsync(
        HttpRequest request,
        string collectionName,
        string fileName,
        bool blocking,
        double timeout)
    {
        _ = request;

        var ct = request.HttpContext.RequestAborted;
        const int pollIntervalMs = 2_000;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeout > 0 ? timeout : 300);

        while (true)
        {
            var summaryText = await summarizationService.GetSummaryTextAsync(collectionName, fileName);

            if (summaryText is not null)
            {
                return Results.Ok(new SummaryResponse(
                    Message: "Summary retrieved successfully.",
                    Status: "SUCCESS",
                    Summary: summaryText,
                    FileName: fileName,
                    CollectionName: collectionName,
                    StartedAt: null,
                    UpdatedAt: DateTimeOffset.UtcNow.ToString("O")));
            }

            if (!blocking || DateTimeOffset.UtcNow >= deadline || ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(pollIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var statusCode = blocking ? StatusCodes.Status404NotFound : StatusCodes.Status202Accepted;
        return Results.Json(new SummaryResponse(
            Message: blocking
                ? $"No summary found for '{fileName}' in collection '{collectionName}' within timeout."
                : "Summary not yet available.",
            Status: "PENDING",
            Summary: string.Empty,
            FileName: fileName,
            CollectionName: collectionName,
            StartedAt: null,
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O")),
            statusCode: statusCode);
    }

    // ── RAG helpers ───────────────────────────────────────────────────────────

    private async Task<(List<ChatMessage> Messages, List<VectorSearchResult> Chunks)> BuildRagMessagesAsync(
        Prompt prompt,
        IReadOnlyList<Message> windowedMessages,
        RetrievalVectorStore retrievalVectorStore,
        RequestRoleServices roleServices,
        CancellationToken cancellationToken)
    {
        var noChunks = (
            windowedMessages.Select(m => new ChatMessage(m.Role, ExtractTextContent(m.Content))).ToList(),
            new List<VectorSearchResult>());

        var userMessages = windowedMessages.Where(m => m.Role == "user").ToList();
        if (userMessages.Count == 0) return noChunks;

        string rawQuery;
        if (config.MultiTurnRetrieverSimple && windowedMessages.Count > 1)
        {
            rawQuery = string.Join(" ",
                windowedMessages.Select(m => ExtractTextContent(m.Content)));
        }
        else
        {
            rawQuery = ExtractTextContent(userMessages.Last().Content);
        }

        // Query rewriting: reformulate the query as a standalone question using chat history
        var retrievalQuery = (prompt.EnableQueryRewriting || config.EnableQueryRewriting)
            ? await roleServices.QueryRewriting.RewriteAsync(
                rawQuery,
                windowedMessages,
                cancellationToken,
                roleServices.QueryRewriterModel)
            : rawQuery;

        var collectionNames = ResolveCollectionNames(prompt.CollectionNames);
        var topK = prompt.VdbTopK > 0 ? prompt.VdbTopK : config.VdbTopK;
        var threshold = prompt.ConfidenceThreshold ?? config.ConfidenceThreshold;
        var rerankerTopK = prompt.RerankerTopK > 0 ? prompt.RerankerTopK : config.RerankerTopK;
        var shouldRerank = prompt.EnableReranker && config.EnableReranker;

        var filterMap = await BuildCollectionFilterMapAsync(
            retrievalQuery,
            collectionNames,
            prompt.FilterExpr?.ToString(),
            prompt.EnableFilterGenerator || config.EnableFilterGenerator,
            retrievalVectorStore.FilterCapabilities,
            roleServices.FilterExpression,
            roleServices.FilterExpressionGeneratorModel,
            cancellationToken);

        var useQueryDecomposition = prompt.EnableQueryDecomposition || config.EnableQueryDecomposition;

        // Retrieval with optional context-relevance reflection loop
        List<VectorSearchResult> contextChunks = [];
        var activeQuery = retrievalQuery;
        QueryDecompositionResult? decompositionResult = null;
        if (useQueryDecomposition)
        {
            if (collectionNames.Count > 1)
            {
                logger.LogWarning(
                    "Query decomposition is limited to one collection; using '{Collection}' and ignoring {IgnoredCount} additional collection(s).",
                    collectionNames[0],
                    collectionNames.Count - 1);
            }

            decompositionResult = await roleServices.QueryDecomposition.RunAsync(
                activeQuery,
                collectionNames[0],
                topK,
                rerankerTopK,
                threshold,
                shouldRerank,
                filterMap.GetValueOrDefault(collectionNames[0]),
                cancellationToken,
                retrievalVectorStore.Store,
                prompt.RerankerEndpoint,
                roleServices.QueryRewriterModel);
            contextChunks = decompositionResult.Chunks.ToList();
        }
        else
        {
            for (int loop = 0; loop < config.ReflectionMaxLoops; loop++)
            {
                IReadOnlyList<VectorSearchResult> searchResults;
                try
                {
                    searchResults = await SearchAcrossCollectionsAsync(
                        retrievalVectorStore.Store,
                        collectionNames,
                        activeQuery,
                        topK,
                        filterMap,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Vector store search failed, falling back to LLM-only generation");
                    return noChunks;
                }

                contextChunks = await ApplyRerankingAsync(
                    activeQuery,
                    searchResults.ToList(),
                    rerankerTopK,
                    shouldRerank,
                    cancellationToken,
                    prompt.RerankerEndpoint,
                    threshold);

                if (!config.EnableReflection || contextChunks.Count == 0)
                    break;

                var contextText = BuildContextString(contextChunks, config.EnableSourceMetadata);
                var (isRelevant, rewrittenQuery) = await roleServices.Reflection.CheckContextRelevanceAsync(
                    activeQuery,
                    contextText,
                    cancellationToken,
                    roleServices.ReflectionModel);

                if (isRelevant || rewrittenQuery is null)
                    break;

                logger.LogInformation(
                    "Context relevance loop {Loop}: retrying with rewritten query.", loop + 1);
                activeQuery = rewrittenQuery;
            }
        }

        logger.LogInformation(
            "RAG retrieved {Count} chunks from {CollectionCount} collection(s) for generation",
            contextChunks.Count,
            collectionNames.Count);

        var result = new List<ChatMessage>();

        if (decompositionResult is not null)
        {
            result.Add(new ChatMessage("system", decompositionResult.FinalSystemPrompt));
            result.Add(new ChatMessage("user", decompositionResult.FinalHumanPrompt));
        }
        else
        {
            var ragContextText = BuildContextString(contextChunks, config.EnableSourceMetadata);
            result.Add(new ChatMessage("system", prompts.RagTemplate.System));
            result.Add(new ChatMessage("user", PromptCatalog.Render(
                prompts.RagTemplate.Human,
                new Dictionary<string, string?>
                {
                    ["context"] = ragContextText
                })));
        }

        result.AddRange(windowedMessages.Select(m =>
            new ChatMessage(m.Role, ExtractTextContent(m.Content))));

        return (result, contextChunks);
    }

    private IReadOnlyList<string> ResolveCollectionNames(IReadOnlyList<string>? requestedCollections)
    {
        var names = requestedCollections?
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return names is { Count: > 0 } ? names : [config.CollectionName];
    }

    private RetrievalVectorStore ResolveRequestVectorStore(
        string? endpoint,
        string? embeddingEndpoint,
        string? embeddingModel,
        HttpRequest request)
    {
        var bearerToken = GetBearerToken(request);
        if (string.IsNullOrWhiteSpace(endpoint)
            && string.IsNullOrWhiteSpace(bearerToken)
            && string.IsNullOrWhiteSpace(embeddingEndpoint)
            && string.IsNullOrWhiteSpace(embeddingModel))
        {
            return new RetrievalVectorStore(vectorStore, null);
        }

        var client = vectorStoreClientFactory.Create(
            endpoint,
            bearerToken,
            embeddingEndpoint,
            embeddingModel);
        return new RetrievalVectorStore(
            client.Store,
            client.FilterCapabilities ?? client.Store as IVectorStoreFilterCapabilities);
    }

    private static string? GetBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }

    private RequestRoleServices ResolveRequestRoleServices(
        string? queryRewriterEndpoint,
        string? queryRewriterModel,
        string? queryRewriterApiKey,
        string? filterExpressionGeneratorEndpoint,
        string? filterExpressionGeneratorModel,
        string? filterExpressionGeneratorApiKey,
        string? reflectionEndpoint,
        string? reflectionModel,
        string? reflectionApiKey)
    {
        var queryOverride = HasRoleOverride(queryRewriterEndpoint, queryRewriterModel, queryRewriterApiKey);
        var filterOverride = HasRoleOverride(
            filterExpressionGeneratorEndpoint,
            filterExpressionGeneratorModel,
            filterExpressionGeneratorApiKey);
        var reflectionOverride = HasRoleOverride(reflectionEndpoint, reflectionModel, reflectionApiKey);

        IChatCompletionService? queryChat = null;
        QueryRewritingService activeQueryRewriter;
        QueryDecompositionService activeQueryDecomposition;
        if (queryOverride)
        {
            queryChat = CreateRoleChatService(
                queryRewriterEndpoint,
                NormalizeRoleOverride(queryRewriterModel) ?? config.QueryRewriterModelOrDefault,
                config.QueryRewriterEndpointOrDefault,
                queryRewriterApiKey,
                config.QueryRewriterApiKeyOrDefault);
            activeQueryRewriter = new QueryRewritingService(
                queryChat,
                config,
                prompts,
                ResolveLogger<QueryRewritingService>());
            activeQueryDecomposition = new QueryDecompositionService(
                queryChat,
                vectorStore,
                rerankerClient,
                config,
                prompts,
                ResolveLogger<QueryDecompositionService>());
        }
        else
        {
            activeQueryRewriter = queryRewritingService;
            activeQueryDecomposition = queryDecompositionService;
        }

        var activeFilterExpression = filterOverride
            ? new FilterExpressionService(
                CreateRoleChatService(
                    filterExpressionGeneratorEndpoint,
                    NormalizeRoleOverride(filterExpressionGeneratorModel) ?? config.FilterExpressionGeneratorModelOrDefault,
                    config.FilterExpressionGeneratorEndpointOrDefault,
                    filterExpressionGeneratorApiKey,
                    config.FilterExpressionGeneratorApiKeyOrDefault),
                serviceProvider.GetService<IVectorStoreFilterCapabilities>()
                    ?? (vectorStore as IVectorStoreFilterCapabilities)
                    ?? new DisabledFilterCapabilities(),
                config,
                prompts,
                ResolveLogger<FilterExpressionService>())
            : filterExpressionService;

        var activeReflection = reflectionOverride
            ? new ReflectionService(
                CreateRoleChatService(
                    reflectionEndpoint,
                    NormalizeRoleOverride(reflectionModel) ?? config.ReflectionModelOrDefault,
                    config.ReflectionEndpointOrDefault,
                    reflectionApiKey,
                    config.ReflectionApiKeyOrDefault),
                config,
                prompts,
                ResolveLogger<ReflectionService>())
            : reflectionService;

        return new RequestRoleServices(
            activeQueryRewriter,
            activeQueryDecomposition,
            activeReflection,
            activeFilterExpression,
            NormalizeRoleOverride(queryRewriterModel),
            NormalizeRoleOverride(filterExpressionGeneratorModel),
            NormalizeRoleOverride(reflectionModel));
    }

    private static bool HasRoleOverride(params string?[] values)
        => values.Any(value => !string.IsNullOrWhiteSpace(value));

    private static string? NormalizeRoleOverride(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private IChatCompletionService CreateRoleChatService(
        string? endpointOverride,
        string model,
        string defaultEndpoint,
        string? apiKeyOverride,
        string? defaultApiKey)
    {
        var endpoint = string.IsNullOrWhiteSpace(endpointOverride)
            ? defaultEndpoint
            : endpointOverride.Trim();
        var apiKey = string.IsNullOrWhiteSpace(apiKeyOverride)
            ? defaultApiKey
            : apiKeyOverride.Trim();

        return chatCompletionClientFactory.Create(
            ResolveChatProvider(endpoint, config.LlmProvider),
            model,
            endpoint,
            apiKey);
    }

    private ILogger<T> ResolveLogger<T>()
        => serviceProvider.GetService<ILogger<T>>() ?? NullLogger<T>.Instance;

    private IChatCompletionService ResolveRequestChatService(
        string? endpoint,
        string model,
        string configuredProvider,
        IChatCompletionService fallbackService)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return fallbackService;
        }

        return chatCompletionClientFactory.Create(
            ResolveChatProvider(endpoint, configuredProvider),
            model,
            endpoint,
            Environment.GetEnvironmentVariable("NVIDIA_API_KEY"));
    }

    private IChatCompletionService ResolveRequestVlmService(Prompt prompt, string model)
    {
        if (!string.IsNullOrWhiteSpace(prompt.VlmEndpoint))
        {
            var configuredProvider = string.IsNullOrWhiteSpace(config.VlmProvider)
                ? config.LlmProvider
                : config.VlmProvider;
            return chatCompletionClientFactory.Create(
                ResolveChatProvider(prompt.VlmEndpoint, configuredProvider),
                model,
                prompt.VlmEndpoint,
                Environment.GetEnvironmentVariable("NVIDIA_API_KEY"));
        }

        return VlmChatService
            ?? throw new InvalidOperationException("VLM inference requested but no VLM endpoint is configured.");
    }

    private static string ResolveChatProvider(string endpoint, string configuredProvider)
    {
        var normalizedProvider = configuredProvider.Trim().ToLowerInvariant();
        var normalizedEndpoint = endpoint.Trim().ToLowerInvariant();

        if (normalizedEndpoint.Contains("11434", StringComparison.Ordinal)
            || normalizedEndpoint.Contains("/api/chat", StringComparison.Ordinal)
            || normalizedEndpoint.Contains("/api/generate", StringComparison.Ordinal))
        {
            return "ollama";
        }

        if (normalizedEndpoint.Contains("/v1", StringComparison.Ordinal)
            || normalizedEndpoint.Contains("chat/completions", StringComparison.Ordinal))
        {
            return "openai";
        }

        return string.IsNullOrWhiteSpace(normalizedProvider)
            ? "openai"
            : normalizedProvider;
    }

    private async Task<Dictionary<string, string?>> BuildCollectionFilterMapAsync(
        string query,
        IReadOnlyList<string> collectionNames,
        string? suppliedFilterExpr,
        bool enableFilterGenerator,
        IVectorStoreFilterCapabilities? filterCapabilitiesOverride,
        FilterExpressionService activeFilterExpressionService,
        string? filterExpressionGeneratorModelOverride,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(suppliedFilterExpr))
        {
            foreach (var collectionName in collectionNames)
            {
                map[collectionName] = suppliedFilterExpr;
            }

            return map;
        }

        if (!enableFilterGenerator)
        {
            foreach (var collectionName in collectionNames)
            {
                map[collectionName] = null;
            }

            return map;
        }

        foreach (var collectionName in collectionNames)
        {
            map[collectionName] = await activeFilterExpressionService.GenerateAsync(
                query,
                collectionName,
                forceEnable: enableFilterGenerator,
                capabilitiesOverride: filterCapabilitiesOverride,
                modelOverride: filterExpressionGeneratorModelOverride,
                cancellationToken);
        }

        return map;
    }

    private async Task<IReadOnlyList<VectorSearchResult>> SearchAcrossCollectionsAsync(
        IVectorStore activeVectorStore,
        IReadOnlyList<string> collectionNames,
        string query,
        int topK,
        IReadOnlyDictionary<string, string?> filterMap,
        CancellationToken cancellationToken)
    {
        var results = new List<VectorSearchResult>();
        foreach (var collectionName in collectionNames)
        {
            try
            {
                var collectionResults = await activeVectorStore.SearchAsync(
                    collectionName,
                    query,
                    topK,
                    filterMap.GetValueOrDefault(collectionName),
                    cancellationToken);
                results.AddRange(collectionResults.Select(result =>
                    WithCollectionMetadata(result, collectionName)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Vector store search failed for collection '{Collection}'.",
                    collectionName);
            }
        }

        return results;
    }

    private static VectorSearchResult WithCollectionMetadata(
        VectorSearchResult result,
        string collectionName)
    {
        var metadata = result.Metadata?.ToDictionary(kv => kv.Key, kv => kv.Value)
            ?? new Dictionary<string, string>();
        metadata["collection_name"] = collectionName;
        return result with { Metadata = metadata };
    }

    private static VectorStoreSearchResultItem ToVectorStoreSearchResultItem(
        VectorSearchResult result,
        string? collectionNameFallback = null)
    {
        var attributes = result.Metadata?
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value)
            ?? new Dictionary<string, object?>();
        var filename = result.Metadata?.GetValueOrDefault("filename") ?? result.Id;
        var collectionName = result.Metadata?.GetValueOrDefault("collection_name")
            ?? collectionNameFallback;

        attributes["document_id"] = result.Id;
        attributes["content"] = result.Text;
        attributes["text"] = result.Text;
        attributes["source"] = filename;
        attributes["document_name"] = filename;
        attributes["score"] = result.Score;

        if (!string.IsNullOrWhiteSpace(collectionName))
        {
            attributes["collection_name"] = collectionName;
        }

        if (!attributes.ContainsKey("document_type"))
        {
            attributes["document_type"] = result.Metadata?.GetValueOrDefault("type") ?? "text";
        }

        return new VectorStoreSearchResultItem(
            FileId: result.Id,
            Filename: filename,
            Score: result.Score,
            Attributes: attributes,
            Content: [new VectorStoreSearchResultContent("text", result.Text)]);
    }

    private static string BuildContextString(
        IReadOnlyList<VectorSearchResult> chunks,
        bool includeSourceMetadata)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < chunks.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("\n---\n");
            }

            if (includeSourceMetadata
                && chunks[i].Metadata?.TryGetValue("filename", out var fn) == true
                && !string.IsNullOrEmpty(fn))
            {
                sb.Append($"[Source: {fn}]\n");
            }

            sb.Append(chunks[i].Text);
        }

        return sb.ToString();
    }

    private static Dictionary<string, object?> BuildOpenAiCompatibleResponse(
        string model,
        string content,
        IReadOnlyDictionary<string, object?>? usage,
        object? citationsPayload = null)
    {
        var response = new Dictionary<string, object?>
        {
                ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
                ["object"] = "chat.completion",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = model,
                ["choices"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["message"] = new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = content
                        },
                        ["finish_reason"] = "stop"
                    }
                },
                ["usage"] = usage ?? new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = 0,
                    ["completion_tokens"] = 0,
                    ["total_tokens"] = 0
                }
        };

        if (citationsPayload is not null)
        {
            response["citations"] = citationsPayload;
        }

        return response;
    }

    private void SetGenerateSpanRequestTags(
        Activity? activity,
        HttpRequest request,
        Prompt prompt,
        string model,
        string templateKey,
        int retrievedContextCount)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("rag.request.path", request.Path.Value ?? string.Empty);
        activity.SetTag("rag.prompt.template", templateKey);
        activity.SetTag("rag.prompt.message_count", prompt.Messages.Count);
        activity.SetTag("rag.knowledge_base.enabled", prompt.UseKnowledgeBase);
        activity.SetTag("rag.collection.count", ResolveCollectionNames(prompt.CollectionNames).Count);
        activity.SetTag("rag.retrieved_context.count", retrievedContextCount);
        activity.SetTag("gen_ai.request.model", model);
    }

    private static async Task WriteSseDeltaAsync(
        HttpResponse response,
        string? content,
        string? reasoningContent,
        CancellationToken cancellationToken)
    {
        var delta = new Dictionary<string, object?>();
        if (content is not null)
        {
            delta["content"] = content;
        }

        if (reasoningContent is not null)
        {
            delta["reasoning_content"] = reasoningContent;
        }

        var payload = new
        {
            choices = new[]
            {
                new
                {
                    delta,
                    finish_reason = (string?)null
                }
            }
        };
        var line = $"data: {JsonSerializer.Serialize(payload)}\n\n";
        await response.WriteAsync(line, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private async Task WriteSseFinalAsync(
        HttpResponse response,
        IReadOnlyList<VectorSearchResult> citationChunks,
        IReadOnlyDictionary<string, object?>? usage,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["choices"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["delta"] = new Dictionary<string, object?> { ["content"] = string.Empty },
                    ["finish_reason"] = "stop"
                }
            },
            ["citations"] = await BuildCitationsPayloadAsync(citationChunks, cancellationToken)
        };
        if (usage is not null)
        {
            payload["usage"] = usage;
            RagTraceAttributes.SetLlmUsageTags(Activity.Current, usage);
        }

        var finalLine = $"data: {JsonSerializer.Serialize(payload)}\n\n";
        await response.WriteAsync(finalLine, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private async Task<object> BuildCitationsPayloadAsync(
        IReadOnlyList<VectorSearchResult> chunks,
        CancellationToken cancellationToken)
    {
        var citations = new List<Dictionary<string, object?>>(chunks.Count);
        foreach (var result in chunks)
        {
            var filename = result.Metadata?.GetValueOrDefault("filename") ?? result.Id;
            var documentType = ResolveCitationDocumentType(result.Metadata);
            var content = result.Text;
            var asset = await citationAssetResolver.ResolveAsync(result, cancellationToken);
            if (asset is not null)
            {
                content = asset.ContentBase64;
                documentType = asset.DocumentType;
            }

            citations.Add(new Dictionary<string, object?>
            {
                ["document_id"] = result.Id,
                ["content"] = content,
                ["text"] = result.Text,
                ["source"] = filename,
                ["document_name"] = filename,
                ["collection_name"] = result.Metadata?.GetValueOrDefault("collection_name"),
                ["document_type"] = documentType,
                ["score"] = result.Score,
                ["metadata"] = BuildCitationMetadata(result, documentType)
            });
        }

        return new { total_results = citations.Count, results = citations };
    }

    private static object BuildEmptyCitationsPayload() =>
        new { total_results = 0, results = Array.Empty<Dictionary<string, object?>>() };

    private static string ResolveCitationDocumentType(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
        {
            return "text";
        }

        if (metadata.TryGetValue("content_metadata.subtype", out var subtype)
            && (subtype.Equals("table", StringComparison.OrdinalIgnoreCase)
                || subtype.Equals("chart", StringComparison.OrdinalIgnoreCase)))
        {
            return subtype;
        }

        if (metadata.TryGetValue("document_type", out var documentType) && !string.IsNullOrWhiteSpace(documentType))
        {
            return documentType;
        }

        if (metadata.TryGetValue("content_metadata.type", out var contentMetadataType)
            && !string.IsNullOrWhiteSpace(contentMetadataType))
        {
            return contentMetadataType;
        }

        var nestedContentType = GetJsonPropertyString(
            GetMetadataString(metadata, "content_metadata"),
            "subtype",
            "type",
            "document_type");
        if (!string.IsNullOrWhiteSpace(nestedContentType))
        {
            return nestedContentType;
        }

        return metadata.GetValueOrDefault("type") ?? "text";
    }

    private static Dictionary<string, object?> BuildCitationMetadata(
        VectorSearchResult result,
        string documentType)
    {
        var metadata = result.Metadata;
        var contentMetadata = BuildContentMetadata(metadata, documentType);
        return new Dictionary<string, object?>
        {
            ["language"] = GetMetadataString(metadata, "language", "content_metadata.language") ?? string.Empty,
            ["date_created"] = GetMetadataString(metadata, "date_created", "content_metadata.date_created") ?? string.Empty,
            ["last_modified"] = GetMetadataString(metadata, "last_modified", "content_metadata.last_modified") ?? string.Empty,
            ["page_number"] = GetMetadataInt(metadata, "page_number", "content_metadata.page_number") ?? 0,
            ["description"] = result.Text,
            ["height"] = GetMetadataInt(metadata, "height", "content_metadata.height") ?? 0,
            ["width"] = GetMetadataInt(metadata, "width", "content_metadata.width") ?? 0,
            ["location"] = GetMetadataObject(metadata, "location", "content_metadata.location") ?? Array.Empty<double>(),
            ["location_max_dimensions"] = GetMetadataObject(metadata, "location_max_dimensions", "content_metadata.location_max_dimensions") ?? Array.Empty<int>(),
            ["source_location"] = GetCitationSourceLocation(metadata) ?? string.Empty,
            ["content_metadata"] = contentMetadata
        };
    }

    private static Dictionary<string, object?> BuildContentMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        string documentType)
    {
        var contentMetadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = documentType
        };
        if (metadata is null)
        {
            return contentMetadata;
        }

        if (metadata.TryGetValue("content_metadata", out var nestedContentMetadata)
            && !string.IsNullOrWhiteSpace(nestedContentMetadata))
        {
            foreach (var (key, value) in ParseJsonObjectProperties(nestedContentMetadata))
            {
                contentMetadata[key] = value;
            }
        }

        foreach (var (key, value) in metadata)
        {
            const string prefix = "content_metadata.";
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var contentKey = key[prefix.Length..];
            if (!string.IsNullOrWhiteSpace(contentKey))
            {
                contentMetadata[contentKey] = ParseMetadataObject(value);
            }
        }

        contentMetadata["type"] = documentType;

        return contentMetadata;
    }

    private static string? GetCitationSourceLocation(IReadOnlyDictionary<string, string>? metadata)
    {
        var direct = GetMetadataString(
            metadata,
            "source_location",
            "source.source_location",
            "stored_image_uri",
            "thumbnail_id",
            "thumbnail_uri",
            "thumbnail_object_name",
            "source.thumbnail_id",
            "source.thumbnail_uri",
            "source.thumbnail_object_name",
            "storage_uri",
            "image_uri",
            "asset_uri");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        return GetJsonPropertyString(
            GetMetadataString(metadata, "source"),
            "source_location",
            "stored_image_uri",
            "thumbnail_id",
            "thumbnail_uri",
            "thumbnail_object_name",
            "storage_uri",
            "image_uri",
            "asset_uri");
    }

    private static object? GetMetadataObject(
        IReadOnlyDictionary<string, string>? metadata,
        params string[] keys)
    {
        var value = GetMetadataString(metadata, keys);
        return value is null ? null : ParseMetadataObject(value);
    }

    private static int? GetMetadataInt(
        IReadOnlyDictionary<string, string>? metadata,
        params string[] keys)
    {
        var value = GetMetadataString(metadata, keys);
        if (value is null)
        {
            return null;
        }

        if (int.TryParse(value, out var parsed))
        {
            return parsed;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Number && root.TryGetInt32(out parsed))
            {
                return parsed;
            }

            if (root.ValueKind == JsonValueKind.String
                && int.TryParse(root.GetString(), out parsed))
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? GetMetadataString(
        IReadOnlyDictionary<string, string>? metadata,
        params string[] keys)
    {
        if (metadata is null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return UnwrapJsonString(value);
            }

            var nestedValue = GetNestedMetadataValue(metadata, key);
            if (!string.IsNullOrWhiteSpace(nestedValue))
            {
                return nestedValue;
            }
        }

        return null;
    }

    private static string? GetNestedMetadataValue(IReadOnlyDictionary<string, string> metadata, string key)
    {
        var dotIndex = key.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex <= 0 || dotIndex == key.Length - 1)
        {
            return null;
        }

        var rootKey = key[..dotIndex];
        var propertyName = key[(dotIndex + 1)..];
        if (!metadata.TryGetValue(rootKey, out var rootValue) || string.IsNullOrWhiteSpace(rootValue))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rootValue);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            return JsonElementToMetadataString(property);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object? ParseMetadataObject(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> ParseJsonObjectProperties(string value)
    {
        var properties = new List<KeyValuePair<string, object?>>();
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return properties;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                properties.Add(new KeyValuePair<string, object?>(
                    property.Name,
                    property.Value.Clone()));
            }
        }
        catch (JsonException)
        {
            return properties;
        }

        return properties;
    }

    private static string? GetJsonPropertyString(string? value, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var key in keys)
            {
                if (!document.RootElement.TryGetProperty(key, out var property))
                {
                    continue;
                }

                var valueString = JsonElementToMetadataString(property);
                if (!string.IsNullOrWhiteSpace(valueString))
                {
                    return valueString;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? JsonElementToMetadataString(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
            JsonValueKind.Array or JsonValueKind.Object => element.GetRawText(),
            _ => null
        };

    private static string UnwrapJsonString(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.String
                ? document.RootElement.GetString() ?? string.Empty
                : value;
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static string ExtractTextContent(object content)
    {
        return content switch
        {
            string s => s,
            JsonElement el when el.ValueKind == JsonValueKind.String
                => el.GetString() ?? string.Empty,
            JsonElement el when el.ValueKind == JsonValueKind.Array
                => string.Join(" ", el.EnumerateArray()
                    .Where(p => p.TryGetProperty("type", out var t) && t.GetString() == "text")
                    .Select(p => p.TryGetProperty("text", out var tx) ? tx.GetString() ?? string.Empty : string.Empty)),
            _ => content?.ToString() ?? string.Empty
        };
    }

    private async Task<List<VectorSearchResult>> ApplyRerankingAsync(
        string query,
        IReadOnlyList<VectorSearchResult> candidates,
        int topK,
        bool shouldRerank,
        CancellationToken cancellationToken,
        string? rerankerEndpoint = null,
        double confidenceThreshold = 0.0)
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
                    "confidence_threshold is set to {Threshold} but reranking is disabled. Confidence threshold filtering requires reranker scores; returning vector-score ordering without confidence filtering.",
                    confidenceThreshold);
            }

            return candidates
                .OrderByDescending(c => c.Score)
                .Take(normalizedTopK)
                .ToList();
        }

        var rerankerSw = Stopwatch.StartNew();
        metrics.RerankerRequests.Add(1);
        try
        {
            var reranked = await rerankerClient.RerankAsync(
                query,
                candidates,
                normalizedTopK,
                cancellationToken,
                rerankerEndpoint);
            metrics.RerankerLatency.Record(rerankerSw.Elapsed.TotalSeconds);
            logger.LogInformation(
                "Reranked {InputCount} chunks via reranker-service; returning {OutputCount}",
                candidates.Count,
                reranked.Count);
            var rerankedList = reranked.ToList();
            if (confidenceThreshold <= 0.0)
            {
                return rerankedList;
            }

            var filtered = rerankedList
                .Where(r => r.Score >= confidenceThreshold)
                .ToList();
            logger.LogInformation(
                "Applied confidence threshold {Threshold} to reranked chunks: {InputCount} -> {OutputCount}.",
                confidenceThreshold,
                rerankedList.Count,
                filtered.Count);
            return filtered;
        }
        catch (Exception ex)
        {
            metrics.RerankerErrors.Add(1);
            metrics.RerankerLatency.Record(rerankerSw.Elapsed.TotalSeconds);
            logger.LogWarning(ex,
                "Reranker service unavailable.");
            throw;
        }
    }
}
