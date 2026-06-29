using System.Diagnostics;
using DotnetRag.Rag.Clients;
using DotnetRag.Rag.Services;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Prompts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotnetRag.Tests.Unit.Services;

public sealed class QueryDecompositionServiceTests
{
    [Fact]
    public void ParseSubqueries_WithMixedPreambleAndNumberedList_PrefersPrefixedLines()
    {
        var result = QueryDecompositionService.ParseSubqueries("""
            Here are useful subqueries:
            1. What was revenue in 2024?
            2) What was margin in 2024?
            Ignore this trailing note.
            """);

        result.Should().Equal(
            "What was revenue in 2024?",
            "What was margin in 2024?");
    }

    [Fact]
    public void ParseSubqueries_WithBullets_StripsBulletPrefixes()
    {
        var result = QueryDecompositionService.ParseSubqueries("""
            - Revenue by quarter
            * Margin by quarter
            """);

        result.Should().Equal("Revenue by quarter", "Margin by quarter");
    }

    [Fact]
    public void ParseSubqueries_WithSingleUnprefixedLine_ReturnsLine()
    {
        var result = QueryDecompositionService.ParseSubqueries("Revenue trend");

        result.Should().ContainSingle().Which.Should().Be("Revenue trend");
    }

    [Fact]
    public void FormatConversationHistory_MatchesPythonSeparator()
    {
        var result = QueryDecompositionService.FormatConversationHistory(
        [
            ("Question A", "Answer A"),
            ("Question B", "Answer B")
        ]);

        result.Should().Be("Question: Question A\nAnswer: Answer A\n\n\nQuestion: Question B\nAnswer: Answer B");
    }

    [Fact]
    public void FormatConversationHistory_WithEmptyHistory_ReturnsEmptyString()
    {
        var result = QueryDecompositionService.FormatConversationHistory([]);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateSubqueriesAsync_RecordsStageSpanWithPromptModelAndUsageTags()
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
                "1. Revenue by quarter\n2. Margin by quarter",
                Usage: new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = 17,
                    ["completion_tokens"] = 6,
                    ["total_tokens"] = 23
                }));
        var service = new QueryDecompositionService(
            chat.Object,
            Mock.Of<IVectorStore>(),
            Mock.Of<IRerankerClient>(),
            new RagServerConfiguration { QueryRewriterModel = "decompose-model" },
            PromptCatalog.Load(null),
            NullLogger<QueryDecompositionService>.Instance);

        var result = await service.GenerateSubqueriesAsync("Compare revenue and margin");

        result.Should().Equal("Revenue by quarter", "Margin by quarter");
        var activity = completed.Last(a =>
            a.DisplayName == "rag.Query Decomposition.token_usage"
            && Equals(a.GetTagItem("gen_ai.request.model"), "decompose-model"));
        activity.GetTagItem("rag.query_decomposition.step").Should().Be("generate_subqueries");
        activity.GetTagItem("rag.prompt.template").Should().Be("query_decomposition_multiquery_prompt");
        activity.GetTagItem("rag.prompt.message_count").Should().Be(2);
        activity.GetTagItem("gen_ai.usage.input_tokens").Should().Be(17);
        activity.GetTagItem("gen_ai.usage.output_tokens").Should().Be(6);
        activity.GetTagItem("llm.usage.total_tokens").Should().Be(23);
    }

    [Fact]
    public async Task RunAsync_WithSingleSubquery_RendersFinalPromptWithEmptyHistoryAndDirectContext()
    {
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse("What was revenue?"));
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(v => v.SearchAsync("docs", "What was revenue?", 5, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("rev-1", "Revenue was $10M.", 0.9)
            ]);
        var service = BuildService(chat.Object, vectorStore.Object);

        var result = await service.RunAsync(
            "What was revenue?",
            "docs",
            topK: 5,
            rerankerTopK: 2,
            threshold: 0.0,
            shouldRerank: false,
            filterExpr: null,
            CancellationToken.None);

        result.ConversationHistory.Should().BeEmpty();
        result.Chunks.Should().ContainSingle().Which.Text.Should().Be("Revenue was $10M.");
        result.FinalHumanPrompt.Should().Contain("What was revenue?");
        result.FinalHumanPrompt.Should().Contain("Revenue was $10M.");
        result.FinalHumanPrompt.Should().NotContain("Answer:");
        chat.Verify(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WithMultipleSubqueries_RendersFinalPromptWithPythonConversationHistory()
    {
        var chat = new Mock<IChatCompletionService>();
        chat.SetupSequence(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse("1. What was revenue?\n2. What was margin?"))
            .ReturnsAsync(new ChatCompletionResponse("Revenue was $10M."))
            .ReturnsAsync(new ChatCompletionResponse("Standalone margin query"))
            .ReturnsAsync(new ChatCompletionResponse("Margin was 42%."))
            .ReturnsAsync(new ChatCompletionResponse("''"));
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(v => v.SearchAsync("docs", "What was revenue?", 5, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("rev-1", "Revenue context.", 0.9)
            ]);
        vectorStore.Setup(v => v.SearchAsync("docs", "Standalone margin query", 5, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("margin-1", "Margin context.", 0.8)
            ]);
        vectorStore.Setup(v => v.SearchAsync("docs", "Compare revenue and margin", 5, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("orig-1", "Original context.", 0.95)
            ]);
        var service = BuildService(
            chat.Object,
            vectorStore.Object,
            new RagServerConfiguration
            {
                QueryRewriterModel = "decompose-model",
                QueryDecompositionRecursionDepth = 2
            });

        var result = await service.RunAsync(
            "Compare revenue and margin",
            "docs",
            topK: 5,
            rerankerTopK: 5,
            threshold: 0.0,
            shouldRerank: false,
            filterExpr: null,
            CancellationToken.None);

        result.ConversationHistory.Should().Be(
            "Question: What was revenue?\nAnswer: Revenue was $10M.\n\n\nQuestion: What was margin?\nAnswer: Margin was 42%.");
        result.FinalHumanPrompt.Should().Contain("Compare revenue and margin");
        result.FinalHumanPrompt.Should().Contain("Question: What was revenue?");
        result.FinalHumanPrompt.Should().Contain("Answer: Margin was 42%.");
        result.FinalHumanPrompt.Should().Contain("Original context.");
        result.FinalHumanPrompt.Should().Contain("Revenue context.");
        result.FinalHumanPrompt.Should().Contain("Margin context.");
        vectorStore.Verify(v => v.SearchAsync("docs", "Standalone margin query", 5, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static QueryDecompositionService BuildService(
        IChatCompletionService chat,
        IVectorStore vectorStore,
        RagServerConfiguration? config = null)
        => new(
            chat,
            vectorStore,
            Mock.Of<IRerankerClient>(),
            config ?? new RagServerConfiguration { QueryRewriterModel = "decompose-model" },
            PromptCatalog.Load(null),
            NullLogger<QueryDecompositionService>.Instance);
}
