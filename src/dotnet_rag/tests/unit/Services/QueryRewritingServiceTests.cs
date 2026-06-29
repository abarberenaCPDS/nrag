using System.Diagnostics;
using DotnetRag.Rag.Services;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Models;
using DotnetRag.Shared.Prompts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotnetRag.Tests.Unit.Services;

public sealed class QueryRewritingServiceTests
{
    static QueryRewritingServiceTests()
    {
        Environment.SetEnvironmentVariable("APP_EMBEDDINGS_DIM", "384");
    }

    private static RagServerConfiguration Config(int history = 5, bool rewriting = true) =>
        new() { };  // uses defaults; ConversationHistory and EnableQueryRewriting are read from env

    private static QueryRewritingService Build(IChatCompletionService chat, int history = 5)
    {
        var cfg = new RagServerConfiguration { ConversationHistory = history };
        return new QueryRewritingService(chat, cfg, PromptCatalog.Load(null), NullLogger<QueryRewritingService>.Instance);
    }

    [Fact]
    public async Task RewriteAsync_ReturnsOriginalQuery_WhenHistoryEmpty()
    {
        var chat = new Mock<IChatCompletionService>();
        var svc = Build(chat.Object);

        var result = await svc.RewriteAsync("what is X?", []);

        result.Should().Be("what is X?");
        chat.Verify(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RewriteAsync_CallsLlm_WhenHistoryPresent()
    {
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse("what is X in context of Y?", null));

        var svc = Build(chat.Object);
        var history = new List<Message> { new("user", "tell me about Y") };

        var result = await svc.RewriteAsync("what is X?", history);

        result.Should().Be("what is X in context of Y?");
    }

    [Fact]
    public async Task RewriteAsync_RecordsStageSpanWithPromptModelAndUsageTags()
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
                "what is X in context of Y?",
                Usage: new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = 11,
                    ["completion_tokens"] = 4,
                    ["total_tokens"] = 15
                }));

        var cfg = new RagServerConfiguration
        {
            ConversationHistory = 3,
            QueryRewriterModel = "rewrite-model"
        };
        var svc = new QueryRewritingService(
            chat.Object,
            cfg,
            PromptCatalog.Load(null),
            NullLogger<QueryRewritingService>.Instance);
        var history = new List<Message>
        {
            new("user", "tell me about Y"),
            new("assistant", "Y is a product"),
            new("user", "what is X?")
        };

        await svc.RewriteAsync("what is X?", history);

        var activity = completed.Last(a =>
            a.DisplayName == "rag.Query Rewriting.token_usage"
            && Equals(a.GetTagItem("gen_ai.request.model"), "rewrite-model"));
        activity.GetTagItem("rag.prompt.template").Should().Be("query_rewriter_prompt");
        activity.GetTagItem("rag.prompt.message_count").Should().Be(2);
        activity.GetTagItem("rag.query_rewriting.history_message_count").Should().Be(2);
        activity.GetTagItem("gen_ai.request.model").Should().Be("rewrite-model");
        activity.GetTagItem("gen_ai.usage.input_tokens").Should().Be(11);
        activity.GetTagItem("gen_ai.usage.output_tokens").Should().Be(4);
        activity.GetTagItem("llm.usage.total_tokens").Should().Be(15);
    }

    [Fact]
    public async Task RewriteAsync_FallsBackToOriginal_OnLlmFailure()
    {
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("timeout"));

        var svc = Build(chat.Object);
        var history = new List<Message> { new("user", "context message") };

        var result = await svc.RewriteAsync("original query", history);

        result.Should().Be("original query");
    }

    [Fact]
    public async Task RewriteAsync_FallsBackToOriginal_WhenLlmReturnsEmpty()
    {
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse(string.Empty, null));

        var svc = Build(chat.Object);
        var history = new List<Message> { new("user", "context") };

        var result = await svc.RewriteAsync("original", history);

        result.Should().Be("original");
    }
}
