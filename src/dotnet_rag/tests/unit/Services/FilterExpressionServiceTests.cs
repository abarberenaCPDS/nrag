using System.Diagnostics;
using DotnetRag.Rag.Services;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Prompts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotnetRag.Tests.Unit.Services;

public sealed class FilterExpressionServiceTests
{
    static FilterExpressionServiceTests()
    {
        Environment.SetEnvironmentVariable("APP_EMBEDDINGS_DIM", "384");
    }

    private static FilterExpressionService Build(
        IChatCompletionService chat,
        IVectorStoreFilterCapabilities? filterCapabilities = null,
        bool enabled = true,
        bool supportsGeneratedFilters = true)
    {
        var capabilities = filterCapabilities ?? BuildCapabilities(supportsGeneratedFilters);
        var cfg = new RagServerConfiguration
        {
            EnableFilterGenerator = enabled
        };
        return new FilterExpressionService(chat, capabilities, cfg, PromptCatalog.Load(null), NullLogger<FilterExpressionService>.Instance);
    }

    private static IVectorStoreFilterCapabilities BuildCapabilities(bool supportsGeneratedFilters)
    {
        var capabilities = new Mock<IVectorStoreFilterCapabilities>();
        capabilities.SetupGet(c => c.SupportsGeneratedFilters).Returns(supportsGeneratedFilters);
        capabilities.SetupGet(c => c.GeneratedFilterPromptKind).Returns(GeneratedFilterPromptKind.Milvus);
        capabilities.Setup(c => c.GetFilterSchemaDescriptionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("year: integer");
        return capabilities.Object;
    }

    [Fact]
    public async Task GenerateAsync_ReturnsNull_WhenDisabledByConfig()
    {
        var chat = new Mock<IChatCompletionService>();
        // Config has ENABLE_FILTER_GENERATOR=false by default
        var svc = Build(chat.Object, enabled: false);

        var result = await svc.GenerateAsync("query", "collection");

        result.Should().BeNull();
        chat.Verify(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsNull_WhenLlmOutputsNone()
    {
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse("None", null));

        var svc = Build(chat.Object);

        // Even when called directly, returns null for "None" response
        var result = await svc.GenerateAsync("hello there", "col");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GenerateAsync_CallsLlm_WhenForceEnabled()
    {
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse("content_metadata[\"year\"] == 2024", null));

        var svc = Build(chat.Object);

        var result = await svc.GenerateAsync("2024 reports", "col", forceEnable: true);

        result.Should().Be("content_metadata[\"year\"] == 2024");
        chat.Verify(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_RecordsStageSpanWithPromptModelAndUsageTags()
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
                "content_metadata[\"year\"] == 2024",
                Usage: new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = 13,
                    ["completion_tokens"] = 5,
                    ["total_tokens"] = 18
                }));
        var cfg = new RagServerConfiguration
        {
            EnableFilterGenerator = true,
            FilterExpressionGeneratorModel = "filter-model"
        };
        var svc = new FilterExpressionService(
            chat.Object,
            BuildCapabilities(supportsGeneratedFilters: true),
            cfg,
            PromptCatalog.Load(null),
            NullLogger<FilterExpressionService>.Instance);

        await svc.GenerateAsync("2024 reports", "reports", forceEnable: true);

        var activity = completed.Last(a =>
            a.DisplayName == "rag.Custom Metadata.token_usage"
            && Equals(a.GetTagItem("gen_ai.request.model"), "filter-model"));
        activity.GetTagItem("rag.prompt.template").Should().Be("filter_expression_generator_prompt_milvus");
        activity.GetTagItem("rag.prompt.message_count").Should().Be(2);
        activity.GetTagItem("rag.filter.collection_name").Should().Be("reports");
        activity.GetTagItem("rag.filter.prompt_kind").Should().Be("Milvus");
        activity.GetTagItem("gen_ai.usage.input_tokens").Should().Be(13);
        activity.GetTagItem("gen_ai.usage.output_tokens").Should().Be(5);
        activity.GetTagItem("llm.usage.total_tokens").Should().Be(18);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsNull_WhenVectorStoreDoesNotSupportGeneratedFilters()
    {
        var chat = new Mock<IChatCompletionService>();
        var svc = Build(chat.Object, supportsGeneratedFilters: false);

        var result = await svc.GenerateAsync("2024 reports", "col", forceEnable: true);

        result.Should().BeNull();
        chat.Verify(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsNull_OnLlmFailure()
    {
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("down"));

        var svc = Build(chat.Object);

        var result = await svc.GenerateAsync("query", "col");

        result.Should().BeNull();
    }
}
