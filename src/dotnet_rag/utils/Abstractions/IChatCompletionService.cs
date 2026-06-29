namespace DotnetRag.Shared.Abstractions;

public interface IChatCompletionService
{
    Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> StreamAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default);

    async IAsyncEnumerable<ChatStreamDelta> StreamDeltasAsync(
        ChatCompletionRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var token in StreamAsync(request, cancellationToken))
        {
            yield return new ChatStreamDelta(Content: token);
        }
    }
}
