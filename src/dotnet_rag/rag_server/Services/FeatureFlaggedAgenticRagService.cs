using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Models;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Rag.Observability;
using DotnetRag.Shared.Prompts;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetRag.Rag.Services;

public sealed class FeatureFlaggedAgenticRagService(
    RagServerConfiguration config,
    IServiceProvider services,
    ILogger<FeatureFlaggedAgenticRagService> logger) : IAgenticRagService
{
    public bool IsRequested(Prompt prompt) =>
        prompt.Agentic == true || (prompt.Agentic is null && config.EnableAgenticRag);

    public async Task<IResult> GenerateAsync(
        AgenticRagInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        if (!config.EnableAgenticRag)
        {
            logger.LogInformation(
                "Agentic RAG request rejected for path {Path} because ENABLE_AGENTIC_RAG is disabled.",
                invocation.RequestPath);
            return Unavailable("Agentic RAG is not enabled in the .NET RAG server.");
        }

        if (invocation.IsStreaming)
        {
            return new AgenticStreamingResult(this, invocation);
        }

        if (string.IsNullOrWhiteSpace(invocation.UserQuery))
        {
            return Results.BadRequest(new { message = "Agentic RAG requires a user query." });
        }

        var orchestration = ResolveOrchestration(invocation);
        var result = await orchestration.RunOneTaskAsync(
            new AgenticOrchestrationRequest(
                invocation.UserQuery,
                invocation.CollectionNames,
                ModelOverride: invocation.Model),
            cancellationToken);
        if (!result.Succeeded)
        {
            return Results.Json(
                new { message = result.Error ?? "Agentic RAG failed to generate a response." },
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Json(BuildOpenAiCompatibleResponse(
            invocation.Prompt.Model ?? config.LlmModel,
            result.Answer,
            BuildCitationsPayload(result.Citations)));
    }

    private static IResult Unavailable(string message)
        => Results.Json(new { message }, statusCode: StatusCodes.Status501NotImplemented);

    private static Dictionary<string, object?> BuildOpenAiCompatibleResponse(
        string model,
        string content,
        object citationsPayload)
        => new()
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
            ["usage"] = new Dictionary<string, object?>
            {
                ["prompt_tokens"] = 0,
                ["completion_tokens"] = 0,
                ["total_tokens"] = 0
            },
            ["citations"] = citationsPayload
        };

    private static object BuildCitationsPayload(IReadOnlyList<AgenticCitation> citations)
        => new
        {
            total_results = citations.Count,
            results = citations.Select(citation =>
            {
                var filename = citation.Metadata.GetValueOrDefault("filename")
                    ?? citation.Metadata.GetValueOrDefault("source")
                    ?? citation.DocumentId;
                return new Dictionary<string, object?>
                {
                    ["document_id"] = citation.DocumentId,
                    ["content"] = citation.Text,
                    ["text"] = citation.Text,
                    ["source"] = filename,
                    ["document_name"] = filename,
                    ["collection_name"] = citation.Metadata.GetValueOrDefault("collection_name"),
                    ["document_type"] = citation.Metadata.GetValueOrDefault("type")
                        ?? citation.Metadata.GetValueOrDefault("content_metadata.type")
                        ?? "text",
                    ["score"] = citation.Score,
                    ["metadata"] = BuildCitationMetadata(citation)
                };
            }).ToArray()
        };

    private static Dictionary<string, object?> BuildCitationMetadata(AgenticCitation citation)
    {
        var metadata = citation.Metadata.ToDictionary(
            pair => pair.Key,
            pair => (object?)pair.Value,
            StringComparer.Ordinal);
        metadata["agentic_task_id"] = citation.TaskId;
        return metadata;
    }

    private IAgenticOrchestrationService ResolveOrchestration(AgenticRagInvocation invocation)
    {
        if (!invocation.HasProviderOverrides)
        {
            return services.GetRequiredService<IAgenticOrchestrationService>();
        }

        var promptCatalog = services.GetRequiredService<PromptCatalog>();
        var metrics = services.GetRequiredService<RagMetrics>();
        var vectorStore = ResolveVectorStore(invocation);
        var chatService = ResolveChatService(invocation);
        var planner = new AgenticPlannerService(
            chatService,
            config,
            promptCatalog,
            services.GetService<ILogger<AgenticPlannerService>>()
                ?? NullLogger<AgenticPlannerService>.Instance,
            metrics);
        var roles = new AgenticRoleService(chatService, config, promptCatalog, metrics);
        return new AgenticOrchestrationService(planner, roles, vectorStore, config, metrics);
    }

    private IVectorStore ResolveVectorStore(AgenticRagInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(invocation.VdbEndpoint)
            && string.IsNullOrWhiteSpace(invocation.BearerToken)
            && string.IsNullOrWhiteSpace(invocation.EmbeddingEndpoint)
            && string.IsNullOrWhiteSpace(invocation.EmbeddingModel))
        {
            return services.GetRequiredService<IVectorStore>();
        }

        return services.GetRequiredService<IVectorStoreClientFactory>()
            .Create(
                invocation.VdbEndpoint,
                invocation.BearerToken,
                invocation.EmbeddingEndpoint,
                invocation.EmbeddingModel)
            .Store;
    }

    private IChatCompletionService ResolveChatService(AgenticRagInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(invocation.LlmEndpoint))
        {
            return services.GetRequiredKeyedService<IChatCompletionService>("main");
        }

        var model = string.IsNullOrWhiteSpace(invocation.Model)
            ? config.LlmModel
            : invocation.Model.Trim();
        var endpoint = invocation.LlmEndpoint.Trim();
        return services.GetRequiredService<IChatCompletionClientFactory>()
            .Create(
                ResolveChatProvider(endpoint, config.LlmProvider),
                model,
                endpoint,
                Environment.GetEnvironmentVariable("NVIDIA_API_KEY"));
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

    private sealed class AgenticStreamingResult(
        FeatureFlaggedAgenticRagService agenticService,
        AgenticRagInvocation invocation) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            if (string.IsNullOrWhiteSpace(invocation.UserQuery))
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(
                    new { message = "Agentic RAG requires a user query." },
                    httpContext.RequestAborted);
                return;
            }

            var orchestration = agenticService.ResolveOrchestration(invocation);
            var response = httpContext.Response;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.Headers["Cache-Control"] = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no";

            var events = Channel.CreateUnbounded<AgenticStreamingChunk>();
            var orchestrationTask = RunOrchestrationAsync(orchestration, events.Writer, httpContext.RequestAborted);
            var wroteAnswerDelta = false;

            await foreach (var streamEvent in events.Reader.ReadAllAsync(httpContext.RequestAborted))
            {
                if (streamEvent.StageEvent is not null)
                {
                    await WriteSseStageAsync(response, streamEvent.StageEvent, httpContext.RequestAborted);
                }

                if (streamEvent.AnswerDelta is not null
                    && (!string.IsNullOrEmpty(streamEvent.AnswerDelta.Content)
                        || !string.IsNullOrEmpty(streamEvent.AnswerDelta.ReasoningContent)))
                {
                    wroteAnswerDelta = wroteAnswerDelta || !string.IsNullOrEmpty(streamEvent.AnswerDelta.Content);
                    await WriteSseDeltaAsync(response, streamEvent.AnswerDelta, httpContext.RequestAborted);
                }
            }

            var result = await orchestrationTask;
            if (!result.Succeeded)
            {
                await WriteSseErrorAsync(
                    response,
                    result.Error ?? "Agentic RAG failed to generate a response.",
                    httpContext.RequestAborted);
                return;
            }

            if (!wroteAnswerDelta)
            {
                const int chunkSize = 20;
                for (var i = 0; i < result.Answer.Length; i += chunkSize)
                {
                    var slice = result.Answer.Substring(i, Math.Min(chunkSize, result.Answer.Length - i));
                    await WriteSseDeltaAsync(response, new ChatStreamDelta(Content: slice), httpContext.RequestAborted);
                }
            }

            await WriteSseFinalAsync(response, BuildCitationsPayload(result.Citations), httpContext.RequestAborted);
        }

        private Task<AgenticOrchestrationResult> RunOrchestrationAsync(
            IAgenticOrchestrationService orchestration,
            ChannelWriter<AgenticStreamingChunk> writer,
            CancellationToken cancellationToken)
            => Task.Run(async () =>
            {
                try
                {
                    var result = await orchestration.RunOneTaskAsync(
                        new AgenticOrchestrationRequest(
                            invocation.UserQuery!,
                            invocation.CollectionNames,
                            ModelOverride: invocation.Model),
                        cancellationToken,
                        stageEvent => writer.TryWrite(AgenticStreamingChunk.FromStage(stageEvent)),
                        delta => writer.TryWrite(AgenticStreamingChunk.FromAnswer(delta)));
                    writer.TryComplete();
                    return result;
                }
                catch (Exception ex)
                {
                    writer.TryComplete(ex);
                    throw;
                }
            }, cancellationToken);

        private static async Task WriteSseStageAsync(
            HttpResponse response,
            AgenticOrchestrationEvent stageEvent,
            CancellationToken cancellationToken)
        {
            var payload = new Dictionary<string, object?>
            {
                ["event_type"] = stageEvent.EventType,
                ["stage"] = stageEvent.Stage,
                ["choices"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["delta"] = new Dictionary<string, object?>
                        {
                            ["reasoning_content"] = stageEvent.Message
                        },
                        ["message"] = new Dictionary<string, object?>
                        {
                            ["reasoning_content"] = stageEvent.Message
                        },
                        ["finish_reason"] = null
                    }
                }
            };
            await response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }

        private static async Task WriteSseErrorAsync(
            HttpResponse response,
            string message,
            CancellationToken cancellationToken)
        {
            var payload = new Dictionary<string, object?>
            {
                ["error"] = new Dictionary<string, object?>
                {
                    ["message"] = message
                },
                ["choices"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["delta"] = new Dictionary<string, object?> { ["content"] = string.Empty },
                        ["finish_reason"] = "stop"
                    }
                }
            };
            await response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }

        private static async Task WriteSseDeltaAsync(
            HttpResponse response,
            ChatStreamDelta delta,
            CancellationToken cancellationToken)
        {
            var deltaPayload = new Dictionary<string, object?>();
            if (!string.IsNullOrEmpty(delta.Content))
            {
                deltaPayload["content"] = delta.Content;
            }

            if (!string.IsNullOrEmpty(delta.ReasoningContent))
            {
                deltaPayload["reasoning_content"] = delta.ReasoningContent;
            }

            var payload = new Dictionary<string, object?>
            {
                ["choices"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["delta"] = deltaPayload,
                        ["finish_reason"] = null
                    }
                }
            };
            await response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }

        private static async Task WriteSseFinalAsync(
            HttpResponse response,
            object citationsPayload,
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
                ["citations"] = citationsPayload,
                ["usage"] = new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = 0,
                    ["completion_tokens"] = 0,
                    ["total_tokens"] = 0
                }
            };
            await response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }

        private sealed record AgenticStreamingChunk(
            AgenticOrchestrationEvent? StageEvent,
            ChatStreamDelta? AnswerDelta)
        {
            public static AgenticStreamingChunk FromStage(AgenticOrchestrationEvent stageEvent)
                => new(stageEvent, null);

            public static AgenticStreamingChunk FromAnswer(ChatStreamDelta delta)
                => new(null, delta);
        }
    }
}
