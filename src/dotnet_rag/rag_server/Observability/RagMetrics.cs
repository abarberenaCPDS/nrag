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

    // Histograms (in seconds)
    public readonly Histogram<double> GenerateLatency;
    public readonly Histogram<double> SearchLatency;
    public readonly Histogram<double> RerankerLatency;
    public readonly Histogram<double> VectorStoreLatency;

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

        _meter.CreateObservableGauge(
            "rag_active_generate_requests",
            () => _activeGenerateRequests,
            description: "Currently active generate requests.");
    }

    public IDisposable TrackActiveRequest() => new ActiveRequestScope(this);

    public void Dispose() => _meter.Dispose();

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
