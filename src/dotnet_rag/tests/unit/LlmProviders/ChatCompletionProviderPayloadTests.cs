using System.Net;
using System.Text;
using System.Text.Json;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.LlmProviders;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetRag.Tests.Unit.LlmProviders;

public sealed class ChatCompletionProviderPayloadTests
{
    [Fact]
    public async Task OpenAiCompleteAsync_WithThinkingControls_SendsNemotronReasoningFields()
    {
        var handler = new RecordingHandler("""
        {
          "choices": [
            { "message": { "content": "answer" } }
          ],
          "usage": {
            "prompt_tokens": 1,
            "completion_tokens": 2,
            "total_tokens": 3
          }
        }
        """);
        var service = new OpenAiChatCompletionService(
            new StaticHttpClientFactory(handler),
            defaultModel: "default-model",
            baseUrl: "http://nim:8000",
            apiKey: "token",
            NullLogger<OpenAiChatCompletionService>.Instance);

        await service.CompleteAsync(new ChatCompletionRequest(
            "vlm-model",
            [new ChatMessage("user", "What is shown?")],
            EnableThinking: true,
            ThinkingTokenBudget: 321));

        handler.RequestBody.Should().NotBeNull();
        using var json = JsonDocument.Parse(handler.RequestBody!);
        json.RootElement.GetProperty("chat_template_kwargs")
            .GetProperty("enable_thinking")
            .GetBoolean()
            .Should()
            .BeTrue();
        json.RootElement.GetProperty("thinking_token_budget").GetInt32().Should().Be(321);
        json.RootElement.TryGetProperty("extra_body", out _).Should().BeFalse();
    }

    [Fact]
    public async Task OllamaCompleteAsync_WithThinkingControls_SendsProviderOptions()
    {
        var handler = new RecordingHandler("""
        {
          "message": { "content": "answer" },
          "prompt_eval_count": 1,
          "eval_count": 2
        }
        """);
        var service = new OllamaChatCompletionService(
            new StaticHttpClientFactory(handler),
            defaultModel: "default-model",
            ollamaBaseUrl: "http://ollama:11434",
            NullLogger<OllamaChatCompletionService>.Instance);

        await service.CompleteAsync(new ChatCompletionRequest(
            "vlm-model",
            [new ChatMessage("user", "What is shown?")],
            EnableThinking: true,
            ThinkingTokenBudget: 321));

        handler.RequestBody.Should().NotBeNull();
        using var json = JsonDocument.Parse(handler.RequestBody!);
        var options = json.RootElement.GetProperty("options");
        options.GetProperty("think").GetBoolean().Should().BeTrue();
        options.GetProperty("thinking_token_budget").GetInt32().Should().Be(321);
    }

    [Fact]
    public async Task OpenAiCompleteAsync_WhenFirstSendFails_RetriesOnceWithFreshRequest()
    {
        var handler = new FailingOnceHandler("""
        {
          "choices": [
            { "message": { "content": "answer after retry" } }
          ],
          "usage": {
            "prompt_tokens": 1,
            "completion_tokens": 2,
            "total_tokens": 3
          }
        }
        """);
        var service = new OpenAiChatCompletionService(
            new StaticHttpClientFactory(handler),
            defaultModel: "default-model",
            baseUrl: "http://nim:8000",
            apiKey: "token",
            NullLogger<OpenAiChatCompletionService>.Instance);

        var result = await service.CompleteAsync(new ChatCompletionRequest(
            "mock-chat",
            [new ChatMessage("user", "retry?")]));

        result.Content.Should().Be("answer after retry");
        handler.SendCount.Should().Be(2);
        handler.RequestBodies.Should().HaveCount(2);
        handler.RequestBodies.Should().OnlyContain(body => body.Contains("retry?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenAiStreamDeltasAsync_WithReasoningDelta_PreservesReasoningContent()
    {
        var handler = new RecordingHandler("""
        data: {"choices":[{"delta":{"reasoning_content":"thinking"},"finish_reason":null}]}

        data: {"choices":[{"delta":{"content":"answer"},"finish_reason":null}]}

        data: {"choices":[],"usage":{"prompt_tokens":4,"completion_tokens":5,"total_tokens":9}}

        data: [DONE]

        """);
        var service = new OpenAiChatCompletionService(
            new StaticHttpClientFactory(handler),
            defaultModel: "default-model",
            baseUrl: "http://nim:8000",
            apiKey: "token",
            NullLogger<OpenAiChatCompletionService>.Instance);

        var deltas = new List<ChatStreamDelta>();
        await foreach (var delta in service.StreamDeltasAsync(new ChatCompletionRequest(
            "vlm-model",
            [new ChatMessage("user", "What is shown?")],
            EnableThinking: true)))
        {
            deltas.Add(delta);
        }

        deltas.Should().HaveCount(3);
        deltas[0].ReasoningContent.Should().Be("thinking");
        deltas[0].Content.Should().BeNull();
        deltas[1].Content.Should().Be("answer");
        deltas[1].ReasoningContent.Should().BeNull();
        deltas[2].Content.Should().BeNull();
        deltas[2].Usage.Should().NotBeNull();
        deltas[2].Usage!["prompt_tokens"].Should().Be(4);
        deltas[2].Usage!["completion_tokens"].Should().Be(5);
        deltas[2].Usage!["total_tokens"].Should().Be(9);

        handler.RequestBody.Should().NotBeNull();
        using var json = JsonDocument.Parse(handler.RequestBody!);
        json.RootElement.GetProperty("stream_options")
            .GetProperty("include_usage")
            .GetBoolean()
            .Should()
            .BeTrue();
    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FailingOnceHandler(string responseBody) : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            if (SendCount == 1)
            {
                throw new HttpRequestException("premature response");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
