using System.Diagnostics;
using DotnetRag.Rag.Services;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Prompts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotnetRag.Tests.Unit.Services;

public sealed class ReflectionServiceTests
{
    static ReflectionServiceTests()
    {
        Environment.SetEnvironmentVariable("APP_EMBEDDINGS_DIM", "384");
    }

    private static ReflectionService Build(IChatCompletionService chat, int contextThreshold = 2, int groundThreshold = 2)
    {
        var cfg = RagServerConfiguration.FromEnvironment();
        return new ReflectionService(chat, cfg, PromptCatalog.Load(null), NullLogger<ReflectionService>.Instance);
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
    public async Task CheckContextRelevanceAsync_RecordsStageSpanWithPromptModelAndUsageTags()
    {
        var completed = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "dotnet-rag-server",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => completed.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse(
                "2",
                Usage: new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = 19,
                    ["completion_tokens"] = 1,
                    ["total_tokens"] = 20
                }));
        var cfg = new RagServerConfiguration { ReflectionModel = "reflection-model" };
        var svc = new ReflectionService(
            chat.Object,
            cfg,
            PromptCatalog.Load(null),
            NullLogger<ReflectionService>.Instance);

        await svc.CheckContextRelevanceAsync("q", "ctx");

        var activity = completed.Last(a =>
            a.DisplayName == "rag.Self Reflection.context_relevance.token_usage"
            && Equals(a.GetTagItem("gen_ai.request.model"), "reflection-model"));
        activity.GetTagItem("rag.reflection.step").Should().Be("context_relevance");
        activity.GetTagItem("rag.prompt.template").Should().Be("reflection_relevance_check_prompt");
        activity.GetTagItem("rag.prompt.message_count").Should().Be(2);
        activity.GetTagItem("gen_ai.usage.input_tokens").Should().Be(19);
        activity.GetTagItem("gen_ai.usage.output_tokens").Should().Be(1);
        activity.GetTagItem("llm.usage.total_tokens").Should().Be(20);
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

    [Fact]
    public async Task CheckContextRelevanceAsync_UsesReflectionModelOverride()
    {
        ChatCompletionRequest? captured = null;
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new ChatCompletionResponse("2", null));

        var cfg = new RagServerConfiguration { ReflectionModel = "reflection-model" };
        var svc = new ReflectionService(
            chat.Object,
            cfg,
            PromptCatalog.Load(null),
            NullLogger<ReflectionService>.Instance);

        await svc.CheckContextRelevanceAsync("q", "ctx");

        captured.Should().NotBeNull();
        captured!.Model.Should().Be("reflection-model");
    }

    private static Mock<IChatCompletionService> MockChat(string response)
    {
        var mock = new Mock<IChatCompletionService>();
        mock.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse(response, null));
        return mock;
    }
}
