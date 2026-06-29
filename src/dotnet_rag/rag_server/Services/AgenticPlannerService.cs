using System.Diagnostics;
using DotnetRag.Rag.Observability;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Models;
using DotnetRag.Shared.Prompts;

namespace DotnetRag.Rag.Services;

public sealed record AgenticPlannerRequest(
    string Query,
    string InitialContext,
    IReadOnlyDictionary<string, string>? ScopeResults = null,
    string? ModelOverride = null);

public interface IAgenticPlannerService
{
    Task<AgenticPlanParseResult> CreatePlanAsync(
        AgenticPlannerRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class AgenticPlannerService(
    IChatCompletionService chatService,
    RagServerConfiguration config,
    PromptCatalog prompts,
    ILogger<AgenticPlannerService> logger,
    RagMetrics? metrics = null) : IAgenticPlannerService
{
    public async Task<AgenticPlanParseResult> CreatePlanAsync(
        AgenticPlannerRequest request,
        CancellationToken cancellationToken = default)
    {
        var maxAttempts = Math.Max(1, config.AgenticPlannerMaxAttempts);
        AgenticPlanParseResult? lastResult = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var chatRequest = BuildChatRequest(request);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var activity = RagMetrics.ActivitySource.StartActivity("rag.Agentic Planner.token_usage");
                activity?.SetTag("rag.agentic.stage", "planner");
                activity?.SetTag("rag.agentic.planner.attempt", attempt);
                activity?.SetTag("rag.prompt.template", "agentic.planner_prompt");
                activity?.SetTag("rag.prompt.message_count", chatRequest.Messages.Count);
                activity?.SetTag("gen_ai.request.model", chatRequest.Model);

                var response = await chatService.CompleteAsync(chatRequest, cancellationToken);
                metrics?.RecordPythonAgenticLlmCall("planner", stopwatch.Elapsed, response.Usage, succeeded: true);
                RagTraceAttributes.SetLlmUsageTags(activity, response.Usage);

                lastResult = AgenticResponseParser.ParsePlan(response.Content);
                if (lastResult.Succeeded && lastResult.Plan is not null)
                {
                    return lastResult with
                    {
                        Plan = TrimTasks(lastResult.Plan)
                    };
                }

                logger.LogWarning(
                    "Agentic planner attempt {Attempt}/{MaxAttempts} returned unparsable output: {Error}",
                    attempt,
                    maxAttempts,
                    lastResult.Error);
            }
            catch (Exception ex)
            {
                metrics?.RecordPythonAgenticLlmCall("planner", stopwatch.Elapsed, null, succeeded: false);
                lastResult = new AgenticPlanParseResult(
                    null,
                    ex.Message,
                    string.Empty);
                logger.LogWarning(
                    ex,
                    "Agentic planner attempt {Attempt}/{MaxAttempts} failed.",
                    attempt,
                    maxAttempts);
            }
        }

        return lastResult ?? new AgenticPlanParseResult(null, "Planner did not return a plan", string.Empty);
    }

    private ChatCompletionRequest BuildChatRequest(AgenticPlannerRequest request)
    {
        var plannerPrompt = prompts.Agentic.PlannerPrompt;
        var values = new Dictionary<string, string?>
        {
            ["query"] = request.Query,
            ["scope_section"] = BuildScopeSection(request.ScopeResults),
            ["initial_context"] = string.IsNullOrWhiteSpace(request.InitialContext)
                ? "(no documents retrieved)"
                : request.InitialContext,
            ["max_plan_tasks"] = Math.Max(1, config.AgenticPlannerMaxTasks).ToString()
        };

        return new ChatCompletionRequest(
            Model: string.IsNullOrWhiteSpace(request.ModelOverride)
                ? config.AgenticPlannerModelOrDefault
                : request.ModelOverride.Trim(),
            Messages:
            [
                new ChatMessage("system", PromptCatalog.Render(plannerPrompt.System, values)),
                new ChatMessage("user", PromptCatalog.Render(plannerPrompt.Human, values))
            ],
            Temperature: config.AgenticPlannerTemperature,
            TopP: config.AgenticPlannerTopP,
            MaxTokens: config.AgenticPlannerMaxTokens);
    }

    private string BuildScopeSection(IReadOnlyDictionary<string, string>? scopeResults)
    {
        if (scopeResults is null || scopeResults.Count == 0)
        {
            return string.Empty;
        }

        var lines = scopeResults
            .Select(pair => $"[{pair.Key}]: {pair.Value}");
        return "Scope Discovery Results (what actually exists in the database):\n"
            + string.Join("\n", lines)
            + "\n"
            + prompts.Agentic.PlannerReplanInstruction;
    }

    private AgenticPlan TrimTasks(AgenticPlan plan)
    {
        var maxTasks = Math.Max(1, config.AgenticPlannerMaxTasks);
        return plan.Tasks.Count <= maxTasks
            ? plan
            : plan with { Tasks = plan.Tasks.Take(maxTasks).ToList() };
    }
}
