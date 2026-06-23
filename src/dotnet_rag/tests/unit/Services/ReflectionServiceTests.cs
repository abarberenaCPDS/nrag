using DotnetRag.Rag.Services;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotnetRag.Tests.Unit.Services;

public sealed class ReflectionServiceTests
{
    private static ReflectionService Build(IChatCompletionService chat, int contextThreshold = 2, int groundThreshold = 2)
    {
        var cfg = RagServerConfiguration.FromEnvironment();
        return new ReflectionService(chat, cfg, NullLogger<ReflectionService>.Instance);
    }

    // ── Context relevance ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("2")]
    [InlineData("score: 2")]
    [InlineData("The score is 2 because...")]
    public async Task CheckContextRelevanceAsync_ReturnsRelevant_WhenScoreIs2(string llmResponse)
    {
        var chat = MockChat(llmResponse);
        var svc = Build(chat.Object);

        var (isRelevant, rewritten) = await svc.CheckContextRelevanceAsync("q", "ctx");

        isRelevant.Should().BeTrue();
        rewritten.Should().BeNull();
    }

    [Fact]
    public async Task CheckContextRelevanceAsync_ReturnsRewrittenQuery_WhenScoreBelow2()
    {
        // First call: score=0, second call: rewritten query
        var chat = new Mock<IChatCompletionService>();
        var seq = 0;
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ChatCompletionResponse(seq++ == 0 ? "0" : "better query", null));

        var svc = Build(chat.Object);

        var (isRelevant, rewritten) = await svc.CheckContextRelevanceAsync("q", "unrelated context");

        isRelevant.Should().BeFalse();
        rewritten.Should().Be("better query");
    }

    [Fact]
    public async Task CheckContextRelevanceAsync_PassesOnLlmFailure()
    {
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("down"));

        var svc = Build(chat.Object);

        var (isRelevant, rewritten) = await svc.CheckContextRelevanceAsync("q", "ctx");

        isRelevant.Should().BeTrue();
        rewritten.Should().BeNull();
    }

    // ── Response groundedness ─────────────────────────────────────────────────

    [Theory]
    [InlineData("2")]
    [InlineData("2 — fully grounded")]
    public async Task CheckResponseGroundednessAsync_ReturnsGrounded_WhenScoreIs2(string llmResponse)
    {
        var chat = MockChat(llmResponse);
        var svc = Build(chat.Object);

        var (isGrounded, improved) = await svc.CheckResponseGroundednessAsync("q", "ctx", "response");

        isGrounded.Should().BeTrue();
        improved.Should().BeNull();
    }

    [Fact]
    public async Task CheckResponseGroundednessAsync_RegeneratesResponse_WhenScoreBelow2()
    {
        var chat = new Mock<IChatCompletionService>();
        var seq = 0;
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ChatCompletionResponse(seq++ == 0 ? "0" : "grounded answer", null));

        var svc = Build(chat.Object);

        var (isGrounded, improved) = await svc.CheckResponseGroundednessAsync("q", "ctx", "bad response");

        isGrounded.Should().BeFalse();
        improved.Should().Be("grounded answer");
    }

    private static Mock<IChatCompletionService> MockChat(string response)
    {
        var mock = new Mock<IChatCompletionService>();
        mock.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse(response, null));
        return mock;
    }
}
