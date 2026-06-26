using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotnetRag.Rag.Clients;
using DotnetRag.Rag.Observability;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Models;
using DotnetRag.Shared.Summarization;

namespace DotnetRag.Rag.Services;

public sealed class RagService(
    RagServerConfiguration config,
    ILogger<RagService> logger,
    IChatCompletionService chatService,
    IVectorStore vectorStore,
    IVectorStoreManagement vectorStoreManagement,
    IRerankerClient rerankerClient,
    ISummarizationService summarizationService,
    QueryRewritingService queryRewritingService,
    ReflectionService reflectionService,
    FilterExpressionService filterExpressionService,
    RagMetrics metrics,
    IServiceProvider serviceProvider)
{
    private static readonly Regex ThinkTokenRegex = new(
        @"<think>.*?</think>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Resolved once on first VLM call; null when VLM not configured
    private IChatCompletionService? VlmChatService =>
        serviceProvider.GetKeyedService<IChatCompletionService>("vlm");

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
                EnableVlmInference: config.EnableVlmInference,
                EnableFilterGenerator: config.EnableFilterGenerator),
            Models: new ModelsDefaults(
                LlmModel: config.LlmModel,
                EmbeddingModel: config.EmbeddingModel,
                RerankerModel: config.RerankerModel,
                VlmModel: config.VlmModel),
            Endpoints: new EndpointsDefaults(
                LlmEndpoint: config.LlmEndpoint,
                EmbeddingEndpoint: config.EmbeddingEndpoint,
                RerankerEndpoint: config.RerankerServiceUrl,
                VlmEndpoint: config.VlmEndpoint,
                VdbEndpoint: config.VectorStoreUrl),
            Providers: new ProvidersDefaults(
                LlmProvider: config.LlmProvider,
                EmbeddingProvider: config.EmbeddingProvider,
                VlmProvider: config.VlmProvider));
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
        var allMessages = prompt.Messages.ToList();

        // Apply conversation history window (0 = unlimited)
        var windowedMessages = config.ConversationHistory > 0
            ? allMessages.TakeLast(config.ConversationHistory).ToList()
            : allMessages;

        // VLM routing: when VLM is enabled and messages contain image content,
        // route to the multimodal endpoint and skip knowledge base retrieval.
        var useVlm = (prompt.EnableVlmInference || config.EnableVlmInference)
            && HasImageContent(allMessages)
            && VlmChatService is not null;

        if (useVlm)
        {
            return await HandleVlmRequestAsync(request, prompt, windowedMessages);
        }

        List<ChatMessage> chatMessages;
        List<VectorSearchResult> contextChunks = [];

        if (prompt.UseKnowledgeBase)
        {
            (chatMessages, contextChunks) = await BuildRagMessagesAsync(
                prompt,
                windowedMessages,
                request.HttpContext.RequestAborted);
        }
        else
        {
            chatMessages = windowedMessages
                .Select(m => new ChatMessage(m.Role, ExtractTextContent(m.Content)))
                .ToList();
        }

        var chatRequest = new ChatCompletionRequest(
            Model: prompt.Model ?? config.LlmModel,
            Messages: chatMessages,
            MaxTokens: prompt.MaxTokens ?? config.MaxTokens,
            Temperature: prompt.Temperature ?? config.Temperature,
            TopP: prompt.TopP ?? config.TopP);

        // /chat/completions → non-streaming JSON (OpenAI-compatible clients)
        if (request.Path.Value?.Contains("chat/completions", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                var chatResponse = await chatService.CompleteAsync(chatRequest, request.HttpContext.RequestAborted);
                var content = config.FilterThinkTokens
                    ? ThinkTokenRegex.Replace(chatResponse.Content, string.Empty).Trim()
                    : chatResponse.Content;

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
                    var (_, improved) = await reflectionService.CheckResponseGroundednessAsync(
                        userQuery, contextText, content, request.HttpContext.RequestAborted);
                    if (improved is not null) content = improved;
                }

                return Results.Json(BuildOpenAiCompatibleResponse(chatRequest.Model, content, chatResponse.Usage));
            }
            catch (HttpRequestException ex)
            {
                logger.LogError(ex, "Failed to invoke LLM");
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        }

        // /generate → SSE streaming (what the React frontend expects)
        await WriteGenerateSseAsync(request.HttpContext, prompt, chatRequest, contextChunks);
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
        List<VectorSearchResult> contextChunks)
    {
        var ct = ctx.RequestAborted;

        // Collect all tokens — needed for guardrail/groundedness checks.
        // For most requests this is a fast in-memory accumulation.
        var tokenBuffer = new System.Text.StringBuilder();
        try
        {
            await foreach (var token in chatService.StreamAsync(chatRequest, ct))
            {
                tokenBuffer.Append(token);
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
            var (_, improved) = await reflectionService.CheckResponseGroundednessAsync(
                userQuery, contextText, fullContent, ct);
            if (improved is not null) fullContent = improved;
        }

        // Now stream the (possibly improved) content token by token
        var resp = ctx.Response;
        resp.ContentType = "text/event-stream; charset=utf-8";
        resp.Headers["Cache-Control"] = "no-cache";
        resp.Headers["X-Accel-Buffering"] = "no";

        // Emit content in reasonably-sized chunks so the frontend can render progressively
        const int chunkSize = 20;
        for (int i = 0; i < fullContent.Length; i += chunkSize)
        {
            var slice = fullContent.Substring(i, Math.Min(chunkSize, fullContent.Length - i));
            var tokenJson = JsonSerializer.Serialize(slice);
            var line = $"data: {{\"choices\":[{{\"delta\":{{\"content\":{tokenJson}}},\"finish_reason\":null}}]}}\n\n";
            await resp.WriteAsync(line, CancellationToken.None);
            await resp.Body.FlushAsync(CancellationToken.None);
        }

        // Final event: finish_reason=stop + citations
        var citations = activeChunks
            .Select(r => new Dictionary<string, object?>
            {
                ["text"] = r.Text,
                ["source"] = r.Metadata?.GetValueOrDefault("filename") ?? r.Id,
                ["document_name"] = r.Metadata?.GetValueOrDefault("filename") ?? r.Id,
                ["document_type"] = "text",
                ["score"] = r.Score
            })
            .ToList();

        var citationsJson = JsonSerializer.Serialize(new { results = citations });
        var finalLine = $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"\"}},\"finish_reason\":\"stop\"}}],\"citations\":{citationsJson}}}\n\n";
        await resp.WriteAsync(finalLine, CancellationToken.None);
        await resp.Body.FlushAsync(CancellationToken.None);
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
        var vlmMessages = BuildVlmMessages(windowedMessages, maxImages);

        var vlmModel = prompt.VlmModel ?? config.VlmModel;
        var vlmRequest = new ChatCompletionRequest(
            Model: vlmModel,
            Messages: vlmMessages,
            MaxTokens: prompt.VlmMaxTokens ?? prompt.MaxTokens ?? config.MaxTokens,
            Temperature: prompt.VlmTemperature ?? prompt.Temperature ?? config.Temperature,
            TopP: prompt.VlmTopP ?? prompt.TopP ?? config.TopP);

        var activeVlmService = VlmChatService!;

        // SSE path
        if (request.Path.Value?.Contains("chat/completions", StringComparison.OrdinalIgnoreCase) != true)
        {
            var ct = request.HttpContext.RequestAborted;
            var tokenBuffer = new StringBuilder();
            var usedFallback = false;

            try
            {
                await foreach (var token in activeVlmService.StreamAsync(vlmRequest, ct))
                    tokenBuffer.Append(token);
            }
            catch (Exception ex) when (config.VlmToLlmFallback)
            {
                logger.LogWarning(ex, "VLM stream failed; falling back to main LLM.");
                usedFallback = true;
                tokenBuffer.Clear();
                var fallbackRequest = vlmRequest with { Model = config.LlmModel };
                await foreach (var token in chatService.StreamAsync(fallbackRequest, ct))
                    tokenBuffer.Append(token);
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
            for (int i = 0; i < content.Length; i += chunkSize)
            {
                var slice = content.Substring(i, Math.Min(chunkSize, content.Length - i));
                var tokenJson = JsonSerializer.Serialize(slice);
                await resp.WriteAsync(
                    $"data: {{\"choices\":[{{\"delta\":{{\"content\":{tokenJson}}},\"finish_reason\":null}}]}}\n\n",
                    CancellationToken.None);
                await resp.Body.FlushAsync(CancellationToken.None);
            }

            await resp.WriteAsync(
                "data: {\"choices\":[{\"delta\":{\"content\":\"\"},\"finish_reason\":\"stop\"}],\"citations\":{\"results\":[]}}\n\n",
                CancellationToken.None);
            await resp.Body.FlushAsync(CancellationToken.None);
            return Results.Empty;
        }

        // Non-streaming path
        try
        {
            var chatResponse = await activeVlmService.CompleteAsync(vlmRequest, request.HttpContext.RequestAborted);
            var content = config.FilterThinkTokens
                ? ThinkTokenRegex.Replace(chatResponse.Content, string.Empty).Trim()
                : chatResponse.Content;
            return Results.Json(BuildOpenAiCompatibleResponse(vlmModel, content, chatResponse.Usage));
        }
        catch (Exception ex) when (config.VlmToLlmFallback)
        {
            logger.LogWarning(ex, "VLM call failed; falling back to main LLM.");
            var fallbackRequest = vlmRequest with { Model = config.LlmModel };
            var chatResponse = await chatService.CompleteAsync(fallbackRequest, request.HttpContext.RequestAborted);
            var content = config.FilterThinkTokens
                ? ThinkTokenRegex.Replace(chatResponse.Content, string.Empty).Trim()
                : chatResponse.Content;
            return Results.Json(BuildOpenAiCompatibleResponse(config.LlmModel, content, chatResponse.Usage));
        }
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
        var rawQuery = data.Query is string s ? s : JsonSerializer.Serialize(data.Query);
        var queryText = data.EnableQueryRewriting && config.ConversationHistory > 0 && data.Messages?.Count > 0
            ? await queryRewritingService.RewriteAsync(rawQuery, data.Messages, request.HttpContext.RequestAborted)
            : rawQuery;

        var collectionName = data.CollectionNames?.FirstOrDefault() ?? config.CollectionName;
        var topK = data.VdbTopK > 0 ? data.VdbTopK : config.VdbTopK;
        var threshold = data.ConfidenceThreshold > 0 ? data.ConfidenceThreshold : config.ConfidenceThreshold;

        try
        {
            var filterExpr = data.FilterExpr?.ToString();
            if (filterExpr is null && config.EnableFilterGenerator)
            {
                filterExpr = await filterExpressionService.GenerateAsync(
                    queryText, collectionName, request.HttpContext.RequestAborted);
            }

            var rawResults = await vectorStore.SearchAsync(collectionName, queryText, topK, filterExpr, request.HttpContext.RequestAborted);
            var shouldRerank = (data.EnableReranker && config.EnableReranker);
            var reranked = await ApplyRerankingAsync(
                queryText,
                rawResults.Where(r => r.Score >= threshold).ToList(),
                data.RerankerTopK > 0 ? data.RerankerTopK : config.RerankerTopK,
                shouldRerank,
                request.HttpContext.RequestAborted);

            var filtered = reranked
                .Select(r => new VectorStoreSearchResultItem(
                    FileId: r.Id,
                    Filename: r.Metadata?.GetValueOrDefault("filename") ?? r.Id,
                    Score: r.Score,
                    Attributes: r.Metadata?
                        .ToDictionary(kv => kv.Key, kv => (object?)kv.Value)
                        ?? new Dictionary<string, object?>(),
                    Content: [new VectorStoreSearchResultContent("text", r.Text)]))
                .ToList();

            logger.LogInformation(
                "Search returned {Count} results from collection '{Collection}'",
                filtered.Count,
                collectionName);

            return Results.Ok(new Citations(Results: filtered, Message: "Search completed."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Vector store search failed for collection '{Collection}'", collectionName);
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
        var queryText = searchRequest.Query is string s ? s : JsonSerializer.Serialize(searchRequest.Query);
        var topK = searchRequest.MaxNumResults > 0 ? searchRequest.MaxNumResults : config.VdbTopK;

        try
        {
            var rawResults = await vectorStore.SearchAsync(vectorStoreId, queryText, topK);
            var shouldRerank = config.EnableReranker;
            var reranked = await ApplyRerankingAsync(
                queryText,
                rawResults.ToList(),
                topK,
                shouldRerank,
                request.HttpContext.RequestAborted);

            var items = reranked.Select(r => new VectorStoreSearchResultItem(
                FileId: r.Id,
                Filename: r.Metadata?.GetValueOrDefault("filename") ?? r.Id,
                Score: r.Score,
                Attributes: r.Metadata?
                    .ToDictionary(kv => kv.Key, kv => (object?)kv.Value)
                    ?? new Dictionary<string, object?>(),
                Content: [new VectorStoreSearchResultContent("text", r.Text)]
            )).ToList();

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
        var retrievalQuery = config.EnableQueryRewriting
            ? await queryRewritingService.RewriteAsync(rawQuery, windowedMessages, cancellationToken)
            : rawQuery;

        var collectionName = prompt.CollectionNames?.FirstOrDefault() ?? config.CollectionName;
        var topK = prompt.VdbTopK > 0 ? prompt.VdbTopK : config.VdbTopK;
        var threshold = prompt.ConfidenceThreshold > 0 ? prompt.ConfidenceThreshold : config.ConfidenceThreshold;
        var rerankerTopK = prompt.RerankerTopK > 0 ? prompt.RerankerTopK : config.RerankerTopK;
        var shouldRerank = prompt.EnableReranker && config.EnableReranker;

        // Filter expression: only for Milvus, only when flag set and prompt didn't supply one
        var filterExpr = prompt.FilterExpr?.ToString();
        if (filterExpr is null && config.EnableFilterGenerator)
        {
            filterExpr = await filterExpressionService.GenerateAsync(
                retrievalQuery, collectionName, cancellationToken);
        }

        // Retrieval with optional context-relevance reflection loop
        List<VectorSearchResult> contextChunks = [];
        var activeQuery = retrievalQuery;
        for (int loop = 0; loop < config.ReflectionMaxLoops; loop++)
        {
            IReadOnlyList<VectorSearchResult> searchResults;
            try
            {
                searchResults = await vectorStore.SearchAsync(collectionName, activeQuery, topK, filterExpr, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Vector store search failed for collection '{Collection}', falling back to LLM-only generation",
                    collectionName);
                return noChunks;
            }

            contextChunks = await ApplyRerankingAsync(
                activeQuery,
                searchResults.Where(r => r.Score >= threshold).ToList(),
                rerankerTopK,
                shouldRerank,
                cancellationToken);

            if (!config.EnableReflection || contextChunks.Count == 0)
                break;

            var contextText = BuildContextString(contextChunks, config.EnableSourceMetadata);
            var (isRelevant, rewrittenQuery) = await reflectionService.CheckContextRelevanceAsync(
                activeQuery, contextText, cancellationToken);

            if (isRelevant || rewrittenQuery is null)
                break;

            logger.LogInformation(
                "Context relevance loop {Loop}: retrying with rewritten query.", loop + 1);
            activeQuery = rewrittenQuery;
        }

        logger.LogInformation(
            "RAG retrieved {Count} chunks from '{Collection}' for generation",
            contextChunks.Count,
            collectionName);

        var result = new List<ChatMessage>();

        if (contextChunks.Count > 0)
        {
            var contextText = BuildContextString(contextChunks, config.EnableSourceMetadata);
            result.Add(new ChatMessage("system",
                $"Use the following context to answer the user's question. " +
                $"If the context is not relevant, rely on your general knowledge and say so.\n\n" +
                $"Context:\n{contextText}"));
        }

        result.AddRange(windowedMessages.Select(m =>
            new ChatMessage(m.Role, ExtractTextContent(m.Content))));

        return (result, contextChunks);
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
        IReadOnlyDictionary<string, object?>? usage)
    {
        return new Dictionary<string, object?>
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
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var normalizedTopK = topK > 0 ? topK : config.RerankerTopK;
        if (!shouldRerank || candidates.Count <= 1)
        {
            return candidates
                .OrderByDescending(c => c.Score)
                .Take(normalizedTopK)
                .ToList();
        }

        var rerankerSw = Stopwatch.StartNew();
        metrics.RerankerRequests.Add(1);
        try
        {
            var reranked = await rerankerClient.RerankAsync(query, candidates, normalizedTopK, cancellationToken);
            metrics.RerankerLatency.Record(rerankerSw.Elapsed.TotalSeconds);
            logger.LogInformation(
                "Reranked {InputCount} chunks via reranker-service; returning {OutputCount}",
                candidates.Count,
                reranked.Count);
            return reranked.ToList();
        }
        catch (Exception ex)
        {
            metrics.RerankerErrors.Add(1);
            metrics.RerankerLatency.Record(rerankerSw.Elapsed.TotalSeconds);
            logger.LogWarning(ex,
                "Reranker service unavailable. Returning vector-score ordering without rerank.");
            return candidates
                .OrderByDescending(c => c.Score)
                .Take(normalizedTopK)
                .ToList();
        }
    }
}
