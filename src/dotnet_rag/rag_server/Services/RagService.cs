using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    ISummarizationService summarizationService)
{
    private static readonly Regex ThinkTokenRegex = new(
        @"<think>.*?</think>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public Task<RAGHealthResponse> HealthAsync(bool checkDependencies)
    {
        var databases = new List<DatabaseHealthInfo>();
        var objectStorage = new List<StorageHealthInfo>();
        var nim = new List<NIMServiceHealthInfo>();

        if (checkDependencies)
        {
            databases.Add(new DatabaseHealthInfo(
                "vector_store",
                config.VectorStoreUrl,
                ServiceStatus.Healthy));

            nim.Add(new NIMServiceHealthInfo(
                "llm",
                config.LlmEndpoint,
                ServiceStatus.Healthy,
                Model: config.LlmModel));
        }

        return Task.FromResult(new RAGHealthResponse(
            Message: "Service is up.",
            Databases: databases,
            ObjectStorage: objectStorage,
            Nim: nim));
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
                RerankerEndpoint: config.RerankerEndpoint,
                VlmEndpoint: config.VlmEndpoint,
                VdbEndpoint: config.VectorStoreUrl));
    }

    public string GetMetrics()
    {
        return "# dotnet rag metrics unavailable in scaffold\n";
    }

    public async Task<IResult> GenerateAsync(HttpRequest request, Prompt prompt)
    {
        var allMessages = prompt.Messages.ToList();

        // Apply conversation history window (0 = unlimited)
        var windowedMessages = config.ConversationHistory > 0
            ? allMessages.TakeLast(config.ConversationHistory).ToList()
            : allMessages;

        List<ChatMessage> chatMessages;
        List<VectorSearchResult> contextChunks = [];

        if (prompt.UseKnowledgeBase)
        {
            (chatMessages, contextChunks) = await BuildRagMessagesAsync(prompt, windowedMessages);
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
                return Results.Json(BuildOpenAiCompatibleResponse(chatRequest.Model, content, chatResponse.Usage));
            }
            catch (HttpRequestException ex)
            {
                logger.LogError(ex, "Failed to invoke LLM");
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        }

        // /generate → SSE streaming (what the React frontend expects)
        await WriteGenerateSseAsync(request.HttpContext, chatRequest, contextChunks);
        return Results.Empty;
    }

    // Streams the LLM response as SSE in the format the frontend's processStream() expects:
    //   data: {"choices":[{"delta":{"content":"token"},"finish_reason":null}]}\n\n
    //   data: {"choices":[{"delta":{"content":""},"finish_reason":"stop"}],"citations":{...}}\n\n
    private async Task WriteGenerateSseAsync(
        HttpContext ctx,
        ChatCompletionRequest chatRequest,
        List<VectorSearchResult> contextChunks)
    {
        var ct = ctx.RequestAborted;
        var resp = ctx.Response;
        resp.ContentType = "text/event-stream; charset=utf-8";
        resp.Headers["Cache-Control"] = "no-cache";
        resp.Headers["X-Accel-Buffering"] = "no";

        try
        {
            await foreach (var token in chatService.StreamAsync(chatRequest, ct))
            {
                var tokenJson = JsonSerializer.Serialize(token);
                var line = $"data: {{\"choices\":[{{\"delta\":{{\"content\":{tokenJson}}},\"finish_reason\":null}}]}}\n\n";
                await resp.WriteAsync(line, ct);
                await resp.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            return; // client disconnected
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "LLM stream failed");
            var errLine = $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"\"}},\"finish_reason\":\"stop\"}}],\"error\":{{\"message\":{JsonSerializer.Serialize(ex.Message)}}}}}\n\n";
            await resp.WriteAsync(errLine, CancellationToken.None);
            await resp.Body.FlushAsync(CancellationToken.None);
            return;
        }

        // Final event: finish_reason=stop + citations from the RAG context chunks
        var citations = contextChunks
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

    public async Task<IResult> SearchAsync(HttpRequest request, DocumentSearch data)
    {
        _ = request;
        var queryText = data.Query is string s ? s : JsonSerializer.Serialize(data.Query);
        var collectionName = data.CollectionNames?.FirstOrDefault() ?? config.CollectionName;
        var topK = data.VdbTopK > 0 ? data.VdbTopK : config.VdbTopK;
        var threshold = data.ConfidenceThreshold > 0 ? data.ConfidenceThreshold : config.ConfidenceThreshold;

        try
        {
            var rawResults = await vectorStore.SearchAsync(collectionName, queryText, topK);

            var filtered = rawResults
                .Where(r => r.Score >= threshold)
                .Take(data.RerankerTopK > 0 ? data.RerankerTopK : config.RerankerTopK)
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
        _ = request;
        var queryText = searchRequest.Query is string s ? s : JsonSerializer.Serialize(searchRequest.Query);
        var topK = searchRequest.MaxNumResults > 0 ? searchRequest.MaxNumResults : config.VdbTopK;

        try
        {
            var rawResults = await vectorStore.SearchAsync(vectorStoreId, queryText, topK);

            var items = rawResults.Select(r => new VectorStoreSearchResultItem(
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
    // Reads from ChromaDB "summary_{collectionName}" instead of the object store.
    public async Task<IResult> GetSummaryAsync(
        HttpRequest request,
        string collectionName,
        string fileName,
        bool blocking,
        double timeout)
    {
        _ = request;
        _ = timeout;

        var summaryText = await summarizationService.GetSummaryTextAsync(
            collectionName, fileName);

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

        // Not yet available — may still be processing in the ingestor
        var statusCode = blocking ? StatusCodes.Status404NotFound : StatusCodes.Status202Accepted;
        return Results.Json(new SummaryResponse(
            Message: blocking
                ? $"No summary found for '{fileName}' in collection '{collectionName}'."
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
        IReadOnlyList<Message> windowedMessages)
    {
        var noChunks = (
            windowedMessages.Select(m => new ChatMessage(m.Role, ExtractTextContent(m.Content))).ToList(),
            new List<VectorSearchResult>());

        var userMessages = windowedMessages.Where(m => m.Role == "user").ToList();
        if (userMessages.Count == 0) return noChunks;

        string retrievalQuery;
        if (config.MultiTurnRetrieverSimple && windowedMessages.Count > 1)
        {
            retrievalQuery = string.Join(" ",
                windowedMessages.Select(m => ExtractTextContent(m.Content)));
        }
        else
        {
            retrievalQuery = ExtractTextContent(userMessages.Last().Content);
        }

        var collectionName = prompt.CollectionNames?.FirstOrDefault() ?? config.CollectionName;
        var topK = prompt.VdbTopK > 0 ? prompt.VdbTopK : config.VdbTopK;
        var threshold = prompt.ConfidenceThreshold > 0 ? prompt.ConfidenceThreshold : config.ConfidenceThreshold;
        var rerankerTopK = prompt.RerankerTopK > 0 ? prompt.RerankerTopK : config.RerankerTopK;

        IReadOnlyList<VectorSearchResult> searchResults;
        try
        {
            searchResults = await vectorStore.SearchAsync(collectionName, retrievalQuery, topK);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Vector store search failed for collection '{Collection}', falling back to LLM-only generation",
                collectionName);
            return noChunks;
        }

        var contextChunks = searchResults
            .Where(r => r.Score >= threshold)
            .Take(rerankerTopK)
            .ToList();

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
}
