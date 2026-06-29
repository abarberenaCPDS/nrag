using System.Diagnostics;
using DotnetRag.Rag.Observability;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;

namespace DotnetRag.Rag.Services;

public sealed record AgenticOrchestrationRequest(
    string Query,
    IReadOnlyList<string> CollectionNames,
    string InitialContext = "",
    string? ModelOverride = null);

public sealed record AgenticOrchestrationEvent(
    string EventType,
    string Stage,
    string Message);

public sealed record AgenticTaskExecution(
    AgenticPlanTask Task,
    AgenticTaskAnswer Answer,
    IReadOnlyList<VectorSearchResult> Documents,
    int Attempts);

public sealed record AgenticCitation(
    string DocumentId,
    string Text,
    double Score,
    string TaskId,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record AgenticOrchestrationResult(
    bool Succeeded,
    string Answer,
    AgenticPlan? Plan,
    IReadOnlyList<AgenticTaskExecution> TaskExecutions,
    IReadOnlyList<AgenticCitation> Citations,
    AgenticVerification? Verification = null,
    string? Error = null);

public interface IAgenticOrchestrationService
{
    Task<AgenticOrchestrationResult> RunOneTaskAsync(
        AgenticOrchestrationRequest request,
        CancellationToken cancellationToken = default,
        Action<AgenticOrchestrationEvent>? onEvent = null,
        Action<ChatStreamDelta>? onAnswerDelta = null);
}

public sealed class AgenticOrchestrationService(
    IAgenticPlannerService planner,
    IAgenticRoleService roles,
    IVectorStore vectorStore,
    RagServerConfiguration config,
    RagMetrics? metrics = null) : IAgenticOrchestrationService
{
    public async Task<AgenticOrchestrationResult> RunOneTaskAsync(
        AgenticOrchestrationRequest request,
        CancellationToken cancellationToken = default,
        Action<AgenticOrchestrationEvent>? onEvent = null,
        Action<ChatStreamDelta>? onAnswerDelta = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var pythonMetricsRecorded = false;
        metrics?.AgenticRequests.Add(1);
        using var activity = RagMetrics.ActivitySource.StartActivity("rag.Agentic Orchestration");
        activity?.SetTag("rag.agentic.stage", "orchestration");
        activity?.SetTag("rag.agentic.mode", "internal_one_task");
        activity?.SetTag("rag.collection.count", request.CollectionNames.Count);

        try
        {
            ReportEvent(onEvent, "stage_start", "plan", "Planning the next retrieval steps...");
            var initialRetrievalStopwatch = Stopwatch.StartNew();
            var initialContext = string.IsNullOrWhiteSpace(request.InitialContext)
                ? await RetrieveDocumentsTextAsync(request.CollectionNames, request.Query, "initial_retrieval", cancellationToken)
                : request.InitialContext;
            metrics?.RecordPythonAgenticStageDuration("initial_retrieval", initialRetrievalStopwatch.Elapsed, true);

            var planStopwatch = Stopwatch.StartNew();
            var (planResult, scopeRoundsUsed) = await CreateExecutablePlanAsync(
                request.Query,
                initialContext,
                request.CollectionNames,
                request.ModelOverride,
                cancellationToken);
            metrics?.RecordPythonAgenticStageDuration("plan", planStopwatch.Elapsed, planResult.Succeeded);
            if (!planResult.Succeeded || planResult.Plan is null)
            {
                activity?.SetStatus(ActivityStatusCode.Error, planResult.Error);
                metrics?.AgenticErrors.Add(1, ErrorReason("planner"));
                metrics?.RecordPythonAgenticError("planner");
                metrics?.RecordPythonAgenticRequest(stopwatch.Elapsed, false, 0, scopeRoundsUsed, 0, null);
                pythonMetricsRecorded = true;
                return new AgenticOrchestrationResult(
                    false,
                    string.Empty,
                    null,
                    [],
                    [],
                    null,
                    planResult.Error ?? "Planner did not return a plan.");
            }

            var plan = planResult.Plan;
            ReportEvent(
                onEvent,
                "stage_end",
                "plan",
                plan.Tasks.Count > 0
                    ? $"Created {plan.Tasks.Count} targeted retrieval task(s)."
                    : "Initial context is sufficient; no further retrieval needed.");

            var tasks = plan.Tasks.Count > 0
                ? plan.Tasks
                : [new AgenticPlanTask("t1", request.Query, string.IsNullOrWhiteSpace(plan.ResolvedQuery) ? request.Query : plan.ResolvedQuery)];

            ReportEvent(onEvent, "stage_start", "execute", $"Executing {tasks.Count} retrieval task(s).");
            var executeStopwatch = Stopwatch.StartNew();
            var executions = (await Task.WhenAll(tasks.Select(task =>
                ExecuteTaskAsync(request.CollectionNames, task, request.ModelOverride, cancellationToken)))).ToList();
            metrics?.RecordPythonAgenticStageDuration("execute", executeStopwatch.Elapsed, true);
            ReportEvent(onEvent, "stage_end", "execute", $"Completed retrieval ({executions.Count} task(s) answered).");

            ReportEvent(onEvent, "stage_start", "synthesize", "Composing the answer...");
            var synthesizeStopwatch = Stopwatch.StartNew();
            var synthesis = await SynthesizeAsync(
                new AgenticSynthesisRequest(
                    request.Query,
                    BuildResolvedSection(plan),
                    plan.SynthesisInstruction ?? string.Empty,
                    FormatTaskExecutions(executions),
                    request.ModelOverride),
                onAnswerDelta,
                cancellationToken);
            metrics?.RecordPythonAgenticStageDuration("synthesize", synthesizeStopwatch.Elapsed, true);
            ReportEvent(onEvent, "stage_end", "synthesize", "Answer ready.");

            ReportEvent(onEvent, "stage_start", "verify", "Reviewing the answer for completeness...");
            var verifyStopwatch = Stopwatch.StartNew();
            var verificationResult = await roles.VerifyAsync(
                new AgenticVerificationRequest(
                    request.Query,
                    BuildResolvedSection(plan),
                    synthesis.Answer,
                    FormatTaskExecutions(executions),
                    request.ModelOverride),
                cancellationToken);
            metrics?.RecordPythonAgenticStageDuration("verify", verifyStopwatch.Elapsed, verificationResult.Succeeded);
            if (verificationResult.Verification is not null)
            {
                if (!string.IsNullOrWhiteSpace(verificationResult.RawResponse))
                {
                    ReportEvent(onEvent, "intermediate_output", "verify", verificationResult.RawResponse);
                }

                ReportEvent(
                    onEvent,
                    "stage_end",
                    "verify",
                    verificationResult.Verification.Passed
                        ? "Answer looks complete."
                        : $"Identified {verificationResult.Verification.Issues.Count} potential gap(s).");
            }

            var followupTaskCount = 0;
            if (verificationResult.Succeeded
                && verificationResult.Verification is { Passed: false, Tasks.Count: > 0 } verification)
            {
                followupTaskCount = verification.Tasks.Count;
                ReportEvent(onEvent, "stage_start", "execute", $"Executing {verification.Tasks.Count} follow-up task(s).");
                var verifyExecuteStopwatch = Stopwatch.StartNew();
                foreach (var followUpTask in verification.Tasks)
                {
                    executions.Add(await ExecuteTaskAsync(
                        request.CollectionNames,
                        followUpTask,
                        request.ModelOverride,
                        cancellationToken));
                }
                metrics?.RecordPythonAgenticStageDuration("verify_execute", verifyExecuteStopwatch.Elapsed, true);
                ReportEvent(
                    onEvent,
                    "stage_end",
                    "execute",
                    $"Completed retrieval ({verification.Tasks.Count} follow-up task(s) answered).");

                ReportEvent(onEvent, "stage_start", "synthesize", "Composing the revised answer...");
                var revisedSynthesizeStopwatch = Stopwatch.StartNew();
                synthesis = await SynthesizeAsync(
                    new AgenticSynthesisRequest(
                        request.Query,
                        BuildResolvedSection(plan),
                        plan.SynthesisInstruction ?? string.Empty,
                        FormatTaskExecutions(executions),
                        request.ModelOverride),
                    onAnswerDelta,
                    cancellationToken);
                metrics?.RecordPythonAgenticStageDuration("synthesize", revisedSynthesizeStopwatch.Elapsed, true);
                ReportEvent(onEvent, "stage_end", "synthesize", "Revised answer ready.");
            }

            var citations = CollateCitations(executions);
            activity?.SetTag("rag.agentic.task_count", executions.Count);
            activity?.SetTag("rag.agentic.followup_task_count", followupTaskCount);
            activity?.SetTag("rag.agentic.citation_count", citations.Count);
            if (verificationResult.Verification is not null)
            {
                activity?.SetTag("rag.agentic.verification_passed", verificationResult.Verification.Passed);
            }

            metrics?.AgenticTasks.Add(executions.Count);
            if (followupTaskCount > 0)
            {
                metrics?.AgenticFollowupTasks.Add(followupTaskCount);
            }
            metrics?.AgenticCitations.Add(citations.Count);
            foreach (var execution in executions)
            {
                metrics?.RecordPythonAgenticTaskResult(
                    execution.Answer.Completeness,
                    execution.Attempts,
                    requestSucceeded: true);
            }
            metrics?.RecordPythonAgenticRequest(
                stopwatch.Elapsed,
                true,
                tasks.Count,
                scopeRoundsUsed,
                followupTaskCount,
                verificationResult.Verification?.Passed);
            pythonMetricsRecorded = true;

            return new AgenticOrchestrationResult(
                true,
                synthesis.Answer,
                plan,
                executions,
                citations,
                verificationResult.Verification);
        }
        catch
        {
            metrics?.AgenticErrors.Add(1, ErrorReason("exception"));
            metrics?.RecordPythonAgenticError("exception");
            if (!pythonMetricsRecorded)
            {
                metrics?.RecordPythonAgenticRequest(stopwatch.Elapsed, false, 0, 0, 0, null);
            }

            throw;
        }
        finally
        {
            metrics?.AgenticLatency.Record(stopwatch.Elapsed.TotalSeconds);
        }
    }

    private static KeyValuePair<string, object?> ErrorReason(string reason)
        => new("reason", reason);

    private static void ReportEvent(
        Action<AgenticOrchestrationEvent>? onEvent,
        string eventType,
        string stage,
        string message)
        => onEvent?.Invoke(new AgenticOrchestrationEvent(eventType, stage, message));

    private Task<AgenticSynthesisResult> SynthesizeAsync(
        AgenticSynthesisRequest request,
        Action<ChatStreamDelta>? onAnswerDelta,
        CancellationToken cancellationToken)
        => onAnswerDelta is null
            ? roles.SynthesizeAsync(request, cancellationToken)
            : roles.SynthesizeStreamingAsync(request, onAnswerDelta, cancellationToken);

    private async Task<(AgenticPlanParseResult PlanResult, int ScopeRoundsUsed)> CreateExecutablePlanAsync(
        string query,
        string initialContext,
        IReadOnlyList<string> collectionNames,
        string? modelOverride,
        CancellationToken cancellationToken)
    {
        var planResult = await planner.CreatePlanAsync(
            new AgenticPlannerRequest(query, initialContext, ModelOverride: modelOverride),
            cancellationToken);

        var scopeRounds = Math.Max(0, config.AgenticPlannerMaxScopeRounds);
        for (var round = 0; round < scopeRounds; round++)
        {
            if (!planResult.Succeeded || planResult.Plan is not { ScopeOnly: true } scopePlan)
            {
                return (planResult, round);
            }

            var scopeResults = await DiscoverScopeAsync(collectionNames, scopePlan.Tasks, cancellationToken);
            if (scopeResults.Count == 0)
            {
                return (planResult, round + 1);
            }

            planResult = await planner.CreatePlanAsync(
                new AgenticPlannerRequest(query, initialContext, scopeResults, modelOverride),
                cancellationToken);
        }

        if (planResult.Succeeded && planResult.Plan is { ScopeOnly: true })
        {
            return (new AgenticPlanParseResult(
                null,
                "Planner requested scope discovery but did not produce an executable plan.",
                planResult.RawResponse), scopeRounds);
        }

        return (planResult, scopeRounds);
    }

    private async Task<IReadOnlyDictionary<string, string>> DiscoverScopeAsync(
        IReadOnlyList<string> collectionNames,
        IReadOnlyList<AgenticPlanTask> tasks,
        CancellationToken cancellationToken)
    {
        var scopeResults = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            var documents = await RetrieveDocumentsAsync(collectionNames, task.Query, cancellationToken);
            scopeResults[task.Id] = FormatDocuments(documents);
        }

        return scopeResults;
    }

    private async Task<AgenticTaskExecution> ExecuteTaskAsync(
        IReadOnlyList<string> collectionNames,
        AgenticPlanTask task,
        string? modelOverride,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, config.AgenticTaskAnswerMaxRetries);
        var currentQuery = task.Query;
        var triedQueries = new List<string>();
        var allDocuments = new List<VectorSearchResult>();
        AgenticTaskAnswer? bestAnswer = null;
        var attempts = 0;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            attempts = attempt;
            var documents = await RetrieveDocumentsAsync(collectionNames, currentQuery, cancellationToken);
            allDocuments.AddRange(documents);
            var question = bestAnswer is { Completeness: "partial" }
                ? BuildRetryQuestion(task.Question, bestAnswer.Answer)
                : task.Question;
            var taskAnswer = await roles.AnswerTaskAsync(
                new AgenticTaskAnswerRequest(question, FormatDocuments(documents), modelOverride),
                cancellationToken);
            bestAnswer = taskAnswer;

            if (string.Equals(taskAnswer.Completeness, "complete", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            triedQueries.Add(BuildTriedQuerySummary(attempt, currentQuery, taskAnswer, documents.Count));
            if (!string.Equals(taskAnswer.Completeness, "partial", StringComparison.OrdinalIgnoreCase)
                || attempt == maxAttempts)
            {
                break;
            }

            var seedResult = await roles.GenerateSeedQueryAsync(
                new AgenticSeedQueryRequest(task.Question, string.Join("\n", triedQueries), modelOverride),
                cancellationToken);
            if (seedResult is null
                || !seedResult.Succeeded
                || seedResult.SeedQuery is null
                || seedResult.SeedQuery.Stop
                || string.IsNullOrWhiteSpace(seedResult.SeedQuery.SeedQuery))
            {
                break;
            }

            currentQuery = seedResult.SeedQuery.SeedQuery;
        }

        return new AgenticTaskExecution(
            task,
            bestAnswer ?? new AgenticTaskAnswer("none", "[NO DATA]", string.Empty),
            allDocuments,
            attempts);
    }

    private static string BuildRetryQuestion(string question, string accumulatedAnswer)
        => question
            + "\n\nIMPORTANT: A prior retrieval already found this partial answer:\n"
            + accumulatedAnswer
            + "\n\nYour job: find the MISSING information and produce a COMPLETE answer "
            + "that merges the prior data with any new data you find in these documents.";

    private static string BuildTriedQuerySummary(
        int attempt,
        string query,
        AgenticTaskAnswer answer,
        int documentCount)
        => $"{attempt}. Query: \"{query}\" -> {answer.Completeness.ToUpperInvariant()} "
            + $"({documentCount} docs) - found: {Truncate(answer.Answer, 80)}"
            + (string.IsNullOrWhiteSpace(answer.Missing) ? string.Empty : $" | still missing: {answer.Missing}");

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private async Task<string> RetrieveDocumentsTextAsync(
        IReadOnlyList<string> collectionNames,
        string query,
        string stage,
        CancellationToken cancellationToken)
        => FormatDocuments(await RetrieveDocumentsAsync(collectionNames, query, cancellationToken, stage));

    private async Task<IReadOnlyList<VectorSearchResult>> RetrieveDocumentsAsync(
        IReadOnlyList<string> collectionNames,
        string query,
        CancellationToken cancellationToken,
        string stage = "execute")
    {
        if (collectionNames.Count == 0)
        {
            metrics?.RecordPythonAgenticRetrieval(stage, 0);
            return [];
        }

        try
        {
            var topK = Math.Max(1, config.VdbTopK);
            var perCollectionResults = new List<VectorSearchResult>();
            foreach (var collectionName in collectionNames.Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                var results = await vectorStore.SearchAsync(collectionName, query, topK, cancellationToken);
                perCollectionResults.AddRange(results);
            }

            var resultLimit = Math.Max(1, config.RerankerTopK);
            var limitedResults = perCollectionResults
                .OrderByDescending(result => result.Score)
                .Take(resultLimit)
                .ToList();
            metrics?.RecordPythonAgenticRetrieval(stage, limitedResults.Count);
            return limitedResults;
        }
        catch
        {
            metrics?.RecordPythonAgenticRetrieval(stage, 0, error: true);
            throw;
        }
    }

    private static string BuildResolvedSection(AgenticPlan plan)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(plan.ResolvedQuery))
        {
            lines.Add($"Resolved Query: {plan.ResolvedQuery}");
        }

        if (!string.IsNullOrWhiteSpace(plan.ScopeResolution))
        {
            lines.Add($"Scope Resolution: {plan.ScopeResolution}");
        }

        return lines.Count == 0 ? string.Empty : string.Join("\n", lines);
    }

    private static string FormatDocuments(IReadOnlyList<VectorSearchResult> documents)
    {
        if (documents.Count == 0)
        {
            return "(no documents retrieved)";
        }

        return string.Join(
            "\n\n",
            documents.Select((document, index) =>
                $"Document {index + 1} (source: {ResolveSource(document)}; score: {document.Score:0.###})\n{document.Text}"));
    }

    private static string FormatTaskExecutions(IReadOnlyList<AgenticTaskExecution> executions)
        => string.Join(
            "\n\n",
            executions.Select(execution =>
                $"Task {execution.Task.Id}: {execution.Task.Question}\n"
                + $"Completeness: {execution.Answer.Completeness}\n"
                + $"Answer: {execution.Answer.Answer}\n"
                + $"Missing: {execution.Answer.Missing}"));

    private static IReadOnlyList<AgenticCitation> CollateCitations(IReadOnlyList<AgenticTaskExecution> executions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var citations = new List<AgenticCitation>();
        foreach (var execution in executions)
        {
            foreach (var document in execution.Documents.OrderByDescending(result => result.Score))
            {
                if (!seen.Add(document.Id))
                {
                    continue;
                }

                citations.Add(new AgenticCitation(
                    document.Id,
                    document.Text,
                    document.Score,
                    execution.Task.Id,
                    document.Metadata ?? new Dictionary<string, string>()));
            }
        }

        return citations;
    }

    private static string ResolveSource(VectorSearchResult document)
    {
        if (document.Metadata is null)
        {
            return document.Id;
        }

        if (document.Metadata.TryGetValue("source", out var source) && !string.IsNullOrWhiteSpace(source))
        {
            return source;
        }

        if (document.Metadata.TryGetValue("filename", out var filename) && !string.IsNullOrWhiteSpace(filename))
        {
            return filename;
        }

        return document.Id;
    }
}
