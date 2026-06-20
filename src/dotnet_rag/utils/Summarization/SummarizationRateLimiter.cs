namespace DotnetRag.Shared.Summarization;

// ORIG: nvidia_rag/utils/summarization.py::get_summarization_semaphore + acquire/release_global_summary_slot
// Redis-based global counter replaced with a process-local SemaphoreSlim.
// For multi-instance deployments, swap this for a distributed lock (Redis, SQL, etc.).
public sealed class SummarizationRateLimiter : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public SummarizationRateLimiter(int maxConcurrency)
    {
        maxConcurrency = Math.Max(1, maxConcurrency);
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public Task WaitAsync(CancellationToken cancellationToken = default) =>
        _semaphore.WaitAsync(cancellationToken);

    public void Release() => _semaphore.Release();

    public void Dispose() => _semaphore.Dispose();
}
