using DotnetRag.Rag.Observability;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Models;
using DotnetRag.Shared.Prompts;
using System.Text;

namespace DotnetRag.Rag.Services;

public sealed record AgenticTaskAnswerRequest(
    string Question,
    string Documents,
    string? ModelOverride = null);

public sealed record AgenticSeedQueryRequest(
    string Question,
    string TriedQueries,
    string? ModelOverride = null);

public sealed record AgenticSynthesisRequest(
    string Query,
    string ResolvedSection,
    string SynthesisInstruction,
    string SubAnswers,
    string? ModelOverride = null);

public sealed record AgenticVerificationRequest(
    string Query,
    string ResolvedQuerySection,
    string Answer,
    string TaskSummary,
    string? ModelOverride = null);

public interface IAgenticRoleService
{
    Task<AgenticTaskAnswer> AnswerTaskAsync(
        AgenticTaskAnswerRequest request,
        CancellationToken cancellationToken = default);

    Task<AgenticSeedQueryParseResult> GenerateSeedQueryAsync(
        AgenticSeedQueryRequest request,
        CancellationToken cancellationToken = default);

    Task<AgenticSynthesisResult> SynthesizeAsync(
        AgenticSynthesisRequest request,
        CancellationToken cancellationToken = default);

    Task<AgenticSynthesisResult> SynthesizeStreamingAsync(
        AgenticSynthesisRequest request,
        Action<ChatStreamDelta> onDelta,
        CancellationToken cancellationToken = default);

    Task<AgenticVerificationParseResult> VerifyAsync(
        AgenticVerificationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class AgenticRoleService(
    IChatCompletionService chatService,
    RagServerConfiguration config,
    PromptCatalog prompts,
    RagMetrics? metrics = null) : IAgenticRoleService
{
    public async Task<AgenticTaskAnswer> AnswerTaskAsync(
        AgenticTaskAnswerRequest request,
        CancellationToken cancellationToken = default)
    {
        var prompt = prompts.Agentic.TaskAnswerPrompt;
        var values = new Dictionary<string, string?>
        {
            ["question"] = request.Question,
            ["documents"] = request.Documents
        };
        var response = await CompleteAsync(
            BuildRequest(
                prompt,
                values,
                ResolveModel(request.ModelOverride, config.AgenticTaskModelOrDefault),
                config.Temperature,
                config.TopP,
                config.MaxTokens),
            "rag.Agentic Task Answer.token_usage",
            "task_answer",
            "agentic.task_answer_prompt",
            cancellationToken);
        return AgenticResponseParser.ParseTaskAnswer(response.Content);
    }

    public async Task<AgenticSeedQueryParseResult> GenerateSeedQueryAsync(
        AgenticSeedQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var prompt = prompts.Agentic.SeedGenerationPrompt;
        var values = new Dictionary<string, string?>
        {
            ["question"] = request.Question,
            ["tried_queries"] = request.TriedQueries
        };
        var response = await CompleteAsync(
            BuildRequest(
                prompt,
                values,
                ResolveModel(request.ModelOverride, config.AgenticSeedGenerationModelOrDefault),
                0.0,
                0.1,
                512),
            "rag.Agentic Seed Generation.token_usage",
            "seed_generation",
            "agentic.seed_gen_prompt",
            cancellationToken);
        return AgenticResponseParser.ParseSeedQuery(response.Content);
    }

    public async Task<AgenticSynthesisResult> SynthesizeAsync(
        AgenticSynthesisRequest request,
        CancellationToken cancellationToken = default)
    {
        var prompt = prompts.Agentic.SynthesisPrompt;
        var values = new Dictionary<string, string?>
        {
            ["query"] = request.Query,
            ["resolved_section"] = request.ResolvedSection,
            ["synthesis_instruction"] = request.SynthesisInstruction,
            ["sub_answers"] = request.SubAnswers
        };
        var response = await CompleteAsync(
            BuildRequest(
                prompt,
                values,
                ResolveModel(request.ModelOverride, config.AgenticSynthesisModelOrDefault),
                config.Temperature,
                config.TopP,
                config.MaxTokens),
            "rag.Agentic Synthesis.token_usage",
            "synthesis",
            "agentic.synthesis_prompt",
            cancellationToken);
        return AgenticResponseParser.ParseSynthesis(response.Content);
    }

    public async Task<AgenticSynthesisResult> SynthesizeStreamingAsync(
        AgenticSynthesisRequest request,
        Action<ChatStreamDelta> onDelta,
        CancellationToken cancellationToken = default)
    {
        var prompt = prompts.Agentic.SynthesisPrompt;
        var values = new Dictionary<string, string?>
        {
            ["query"] = request.Query,
            ["resolved_section"] = request.ResolvedSection,
            ["synthesis_instruction"] = request.SynthesisInstruction,
            ["sub_answers"] = request.SubAnswers
        };
        var response = await CompleteStreamingAsync(
            BuildRequest(
                prompt,
                values,
                ResolveModel(request.ModelOverride, config.AgenticSynthesisModelOrDefault),
                config.Temperature,
                config.TopP,
                config.MaxTokens),
            "rag.Agentic Synthesis.token_usage",
            "synthesis",
            "agentic.synthesis_prompt",
            onDelta,
            cancellationToken);
        return AgenticResponseParser.ParseSynthesis(response.Content);
    }

    public async Task<AgenticVerificationParseResult> VerifyAsync(
        AgenticVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        var prompt = prompts.Agentic.VerificationPrompt;
        var values = new Dictionary<string, string?>
        {
            ["query"] = request.Query,
            ["resolved_query_section"] = request.ResolvedQuerySection,
            ["answer"] = request.Answer,
            ["task_summary"] = request.TaskSummary,
            ["max_verification_tasks"] = Math.Max(0, config.AgenticVerificationMaxTasks).ToString()
        };
        var response = await CompleteAsync(
            BuildRequest(
                prompt,
                values,
                ResolveModel(request.ModelOverride, config.AgenticPlannerModelOrDefault),
                0.0,
                0.1,
                1024),
            "rag.Agentic Verification.token_usage",
            "verification",
            "agentic.verification_prompt",
            cancellationToken);
        return AgenticResponseParser.ParseVerification(response.Content, config.AgenticVerificationMaxTasks);
    }

    private static string ResolveModel(string? modelOverride, string fallback)
        => string.IsNullOrWhiteSpace(modelOverride)
            ? fallback
            : modelOverride.Trim();

    private static ChatCompletionRequest BuildRequest(
        PromptSection prompt,
        IReadOnlyDictionary<string, string?> values,
        string model,
        double temperature,
        double topP,
        int maxTokens)
        => new(
            Model: model,
            Messages:
            [
                new ChatMessage("system", PromptCatalog.Render(prompt.System, values)),
                new ChatMessage("user", PromptCatalog.Render(prompt.Human, values))
            ],
            Temperature: temperature,
            TopP: topP,
            MaxTokens: maxTokens);

    private async Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        string spanName,
        string stage,
        string promptTemplate,
        CancellationToken cancellationToken)
    {
        using var activity = RagMetrics.ActivitySource.StartActivity(spanName);
        activity?.SetTag("rag.agentic.stage", stage);
        activity?.SetTag("rag.prompt.template", promptTemplate);
        activity?.SetTag("rag.prompt.message_count", request.Messages.Count);
        activity?.SetTag("gen_ai.request.model", request.Model);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await chatService.CompleteAsync(request, cancellationToken);
            RagTraceAttributes.SetLlmUsageTags(activity, response.Usage);
            metrics?.RecordPythonAgenticLlmCall(stage, stopwatch.Elapsed, response.Usage, succeeded: true);
            return response;
        }
        catch
        {
            metrics?.RecordPythonAgenticLlmCall(stage, stopwatch.Elapsed, null, succeeded: false);
            throw;
        }
    }

    private async Task<ChatCompletionResponse> CompleteStreamingAsync(
        ChatCompletionRequest request,
        string spanName,
        string stage,
        string promptTemplate,
        Action<ChatStreamDelta> onDelta,
        CancellationToken cancellationToken)
    {
        using var activity = RagMetrics.ActivitySource.StartActivity(spanName);
        activity?.SetTag("rag.agentic.stage", stage);
        activity?.SetTag("rag.prompt.template", promptTemplate);
        activity?.SetTag("rag.prompt.message_count", request.Messages.Count);
        activity?.SetTag("gen_ai.request.model", request.Model);

        var content = new StringBuilder();
        IReadOnlyDictionary<string, object?>? usage = null;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await foreach (var delta in chatService.StreamDeltasAsync(request, cancellationToken))
            {
                if (!string.IsNullOrEmpty(delta.Content))
                {
                    content.Append(delta.Content);
                }

                if (delta.Usage is not null)
                {
                    usage = delta.Usage;
                }

                onDelta(delta);
            }

            RagTraceAttributes.SetLlmUsageTags(activity, usage);
            metrics?.RecordPythonAgenticLlmCall(stage, stopwatch.Elapsed, usage, succeeded: true);
            return new ChatCompletionResponse(Content: content.ToString(), Usage: usage);
        }
        catch
        {
            metrics?.RecordPythonAgenticLlmCall(stage, stopwatch.Elapsed, usage, succeeded: false);
            throw;
        }
    }
}
