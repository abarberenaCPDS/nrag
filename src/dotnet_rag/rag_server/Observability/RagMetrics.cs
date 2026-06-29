using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DotnetRag.Rag.Observability;

// ORIG: nvidia_rag/rag_server/tracing.py — Prometheus counters and histograms.
// Uses .NET built-in System.Diagnostics.Metrics so the OTel SDK just wires up export.
public sealed class RagMetrics : IDisposable
{
    private readonly Meter _meter;

    // Tracing activity source for distributed spans
    public static readonly ActivitySource ActivitySource =
        new(ObservabilityExtensions.ActivitySourceName, "1.0.0");

    // Counters
    public readonly Counter<long> GenerateRequests;
    public readonly Counter<long> GenerateErrors;
    public readonly Counter<long> SearchRequests;
    public readonly Counter<long> SearchErrors;
    public readonly Counter<long> RerankerRequests;
    public readonly Counter<long> RerankerErrors;
    public readonly Counter<long> VectorStoreRequests;
    public readonly Counter<long> VectorStoreErrors;
    public readonly Counter<long> AgenticRequests;
    public readonly Counter<long> AgenticErrors;
    public readonly Counter<long> AgenticTasks;
    public readonly Counter<long> AgenticFollowupTasks;
    public readonly Counter<long> AgenticCitations;
    public readonly Counter<long> PythonAgenticRequests;
    public readonly Counter<long> PythonAgenticErrors;
    public readonly Counter<long> PythonAgenticVerification;
    public readonly Counter<long> PythonAgenticRetrievalCalls;
    public readonly Counter<long> PythonAgenticTaskResults;
    public readonly Counter<long> PythonAgenticLlmCalls;
    public readonly Counter<long> PythonAgenticLlmTokens;

    // Histograms (in seconds)
    public readonly Histogram<double> GenerateLatency;
    public readonly Histogram<double> SearchLatency;
    public readonly Histogram<double> RerankerLatency;
    public readonly Histogram<double> VectorStoreLatency;
    public readonly Histogram<double> AgenticLatency;
    public readonly Histogram<double> PythonAgenticRequestDurationMs;
    public readonly Histogram<double> PythonAgenticStageDurationMs;
    public readonly Histogram<long> PythonAgenticPlanTasks;
    public readonly Histogram<long> PythonAgenticScopeRounds;
    public readonly Histogram<long> PythonAgenticVerificationFollowupTasks;
    public readonly Histogram<long> PythonAgenticRetrievedChunks;
    public readonly Histogram<long> PythonAgenticTaskAttempts;
    public readonly Histogram<double> PythonAgenticLlmCallDurationMs;

    // Gauges (observable)
    private int _activeGenerateRequests;

    public RagMetrics()
    {
        _meter = new Meter(ObservabilityExtensions.MeterName, "1.0.0");

        GenerateRequests = _meter.CreateCounter<long>(
            "rag_generate_requests_total",
            description: "Total RAG generate requests.");
        GenerateErrors = _meter.CreateCounter<long>(
            "rag_generate_errors_total",
            description: "Total RAG generate errors.");
        SearchRequests = _meter.CreateCounter<long>(
            "rag_search_requests_total",
            description: "Total RAG search requests.");
        SearchErrors = _meter.CreateCounter<long>(
            "rag_search_errors_total",
            description: "Total RAG search errors.");
        RerankerRequests = _meter.CreateCounter<long>(
            "rag_reranker_requests_total",
            description: "Total reranker requests.");
        RerankerErrors = _meter.CreateCounter<long>(
            "rag_reranker_errors_total",
            description: "Total reranker errors.");
        VectorStoreRequests = _meter.CreateCounter<long>(
            "rag_vector_store_requests_total",
            description: "Total vector store search requests.");
        VectorStoreErrors = _meter.CreateCounter<long>(
            "rag_vector_store_errors_total",
            description: "Total vector store errors.");
        AgenticRequests = _meter.CreateCounter<long>(
            "rag_agentic_requests_total",
            description: "Total Agentic RAG orchestration requests.");
        AgenticErrors = _meter.CreateCounter<long>(
            "rag_agentic_errors_total",
            description: "Total Agentic RAG orchestration errors.");
        AgenticTasks = _meter.CreateCounter<long>(
            "rag_agentic_tasks_total",
            description: "Total Agentic RAG retrieval tasks executed.");
        AgenticFollowupTasks = _meter.CreateCounter<long>(
            "rag_agentic_followup_tasks_total",
            description: "Total Agentic RAG verification follow-up tasks executed.");
        AgenticCitations = _meter.CreateCounter<long>(
            "rag_agentic_citations_total",
            description: "Total Agentic RAG citations returned.");
        PythonAgenticRequests = _meter.CreateCounter<long>(
            "agentic_requests_total",
            description: "Total Agentic RAG requests.");
        PythonAgenticErrors = _meter.CreateCounter<long>(
            "agentic_errors_total",
            description: "Agentic RAG errors.");
        PythonAgenticVerification = _meter.CreateCounter<long>(
            "agentic_verification_total",
            description: "Agentic RAG verification outcomes.");
        PythonAgenticRetrievalCalls = _meter.CreateCounter<long>(
            "agentic_retrieval_calls_total",
            description: "Agentic RAG retrieval calls.");
        PythonAgenticTaskResults = _meter.CreateCounter<long>(
            "agentic_task_results_total",
            description: "Agentic RAG task outcomes.");
        PythonAgenticLlmCalls = _meter.CreateCounter<long>(
            "agentic_llm_calls_total",
            description: "Agentic RAG LLM calls.");
        PythonAgenticLlmTokens = _meter.CreateCounter<long>(
            "agentic_llm_tokens_total",
            description: "Agentic RAG LLM token usage.");

        GenerateLatency = _meter.CreateHistogram<double>(
            "rag_generate_duration_seconds",
            unit: "s",
            description: "RAG generate request latency.");
        SearchLatency = _meter.CreateHistogram<double>(
            "rag_search_duration_seconds",
            unit: "s",
            description: "RAG search request latency.");
        RerankerLatency = _meter.CreateHistogram<double>(
            "rag_reranker_duration_seconds",
            unit: "s",
            description: "Reranker call latency.");
        VectorStoreLatency = _meter.CreateHistogram<double>(
            "rag_vector_store_duration_seconds",
            unit: "s",
            description: "Vector store search latency.");
        AgenticLatency = _meter.CreateHistogram<double>(
            "rag_agentic_duration_seconds",
            unit: "s",
            description: "Agentic RAG orchestration latency.");
        PythonAgenticRequestDurationMs = _meter.CreateHistogram<double>(
            "agentic_request_duration_ms",
            unit: "ms",
            description: "Agentic RAG request duration in milliseconds.");
        PythonAgenticStageDurationMs = _meter.CreateHistogram<double>(
            "agentic_stage_duration_ms",
            unit: "ms",
            description: "Agentic RAG graph stage duration in milliseconds.");
        PythonAgenticPlanTasks = _meter.CreateHistogram<long>(
            "agentic_plan_tasks",
            description: "Agentic RAG planned task count per request.");
        PythonAgenticScopeRounds = _meter.CreateHistogram<long>(
            "agentic_scope_rounds",
            description: "Agentic RAG scope discovery rounds per request.");
        PythonAgenticVerificationFollowupTasks = _meter.CreateHistogram<long>(
            "agentic_verification_followup_tasks",
            description: "Agentic RAG verification follow-up task count.");
        PythonAgenticRetrievedChunks = _meter.CreateHistogram<long>(
            "agentic_retrieved_chunks",
            description: "Agentic RAG retrieved chunks per retrieval call.");
        PythonAgenticTaskAttempts = _meter.CreateHistogram<long>(
            "agentic_task_attempts",
            description: "Agentic RAG task attempts.");
        PythonAgenticLlmCallDurationMs = _meter.CreateHistogram<double>(
            "agentic_llm_call_duration_ms",
            unit: "ms",
            description: "Agentic RAG LLM call duration in milliseconds.");

        _meter.CreateObservableGauge(
            "rag_active_generate_requests",
            () => _activeGenerateRequests,
            description: "Currently active generate requests.");
    }

    public IDisposable TrackActiveRequest() => new ActiveRequestScope(this);

    public void RecordPythonAgenticRequest(
        TimeSpan elapsed,
        bool succeeded,
        int plannedTaskCount,
        int scopeRoundCount,
        int followupTaskCount,
        bool? verificationPassed)
    {
        var status = succeeded ? "success" : "error";
        var requestTags = new KeyValuePair<string, object?>[]
        {
            new("status", status),
            new("verification_enabled", "true")
        };

        PythonAgenticRequests.Add(1, requestTags);
        PythonAgenticRequestDurationMs.Record(elapsed.TotalMilliseconds, requestTags);
        PythonAgenticPlanTasks.Record(
            Math.Max(0, plannedTaskCount),
            new KeyValuePair<string, object?>("plan_type", "answer"),
            new KeyValuePair<string, object?>("status", status));
        PythonAgenticScopeRounds.Record(
            Math.Max(0, scopeRoundCount),
            new KeyValuePair<string, object?>("status", status));

        var verificationResult = verificationPassed switch
        {
            true => "passed",
            false => "failed",
            null when succeeded => "skipped",
            _ => "skipped"
        };
        PythonAgenticVerification.Add(
            1,
            new KeyValuePair<string, object?>("result", verificationResult),
            new KeyValuePair<string, object?>("status", status));
        PythonAgenticVerificationFollowupTasks.Record(
            Math.Max(0, followupTaskCount),
            new KeyValuePair<string, object?>("status", status));
    }

    public void RecordPythonAgenticError(string stage)
        => PythonAgenticErrors.Add(1, new KeyValuePair<string, object?>("stage", stage));

    public void RecordPythonAgenticStageDuration(string stage, TimeSpan elapsed, bool requestSucceeded)
        => PythonAgenticStageDurationMs.Record(
            elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("stage", stage),
            new KeyValuePair<string, object?>("status", requestSucceeded ? "success" : "error"));

    public void RecordPythonAgenticRetrieval(string stage, int chunkCount, bool error = false)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("stage", stage),
            new("status", error ? "error" : "success")
        };
        PythonAgenticRetrievalCalls.Add(1, tags);
        PythonAgenticRetrievedChunks.Record(Math.Max(0, chunkCount), tags);
        if (error)
        {
            RecordPythonAgenticError(stage);
        }
    }

    public void RecordPythonAgenticTaskResult(string result, int attempts, bool requestSucceeded)
    {
        var normalizedResult = NormalizeAgenticTaskResult(result);
        var tags = new KeyValuePair<string, object?>[]
        {
            new("result", normalizedResult),
            new("status", requestSucceeded ? "success" : "error")
        };
        PythonAgenticTaskResults.Add(1, tags);
        PythonAgenticTaskAttempts.Record(Math.Max(0, attempts), tags);
    }

    public void RecordPythonAgenticLlmCall(
        string role,
        TimeSpan elapsed,
        IReadOnlyDictionary<string, object?>? usage,
        bool succeeded)
    {
        var normalizedRole = NormalizeAgenticLlmRole(role);
        var tags = new KeyValuePair<string, object?>[]
        {
            new("role", normalizedRole),
            new("status", succeeded ? "success" : "error")
        };
        PythonAgenticLlmCalls.Add(1, tags);
        PythonAgenticLlmCallDurationMs.Record(elapsed.TotalMilliseconds, tags);

        var inputTokens = GetUsageInt(usage, "prompt_tokens", "input_tokens");
        if (inputTokens > 0)
        {
            PythonAgenticLlmTokens.Add(
                inputTokens,
                new KeyValuePair<string, object?>("role", normalizedRole),
                new KeyValuePair<string, object?>("type", "input"),
                new KeyValuePair<string, object?>("status", succeeded ? "success" : "error"));
        }

        var outputTokens = GetUsageInt(usage, "completion_tokens", "output_tokens");
        if (outputTokens > 0)
        {
            PythonAgenticLlmTokens.Add(
                outputTokens,
                new KeyValuePair<string, object?>("role", normalizedRole),
                new KeyValuePair<string, object?>("type", "output"),
                new KeyValuePair<string, object?>("status", succeeded ? "success" : "error"));
        }
    }

    public void Dispose() => _meter.Dispose();

    private static string NormalizeAgenticTaskResult(string result)
    {
        var normalized = (result ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "complete" or "answered" => "answered",
            "none" or "no_data" => "no_data",
            "error" => "error",
            _ => "unknown"
        };
    }

    private static string NormalizeAgenticLlmRole(string role)
    {
        var normalized = (role ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "task_answer" => "task_answer",
            "seed_generation" or "seed_generator" => "seed_generator",
            "synthesis" => "synthesis",
            "verification" => "verification",
            "planner" => "planner",
            _ => "unknown"
        };
    }

    private static long GetUsageInt(IReadOnlyDictionary<string, object?>? usage, params string[] keys)
    {
        if (usage is null)
        {
            return 0;
        }

        foreach (var key in keys)
        {
            if (!usage.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            switch (value)
            {
                case int intValue:
                    return intValue;
                case long longValue:
                    return longValue;
                case double doubleValue:
                    return (long)doubleValue;
                case string stringValue when long.TryParse(stringValue, out var parsed):
                    return parsed;
                case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } element
                    when element.TryGetInt64(out var parsed):
                    return parsed;
            }
        }

        return 0;
    }

    private sealed class ActiveRequestScope : IDisposable
    {
        private readonly RagMetrics _metrics;

        public ActiveRequestScope(RagMetrics metrics)
        {
            _metrics = metrics;
            Interlocked.Increment(ref metrics._activeGenerateRequests);
        }

        public void Dispose() => Interlocked.Decrement(ref _metrics._activeGenerateRequests);
    }
}
