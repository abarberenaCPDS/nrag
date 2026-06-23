using DotnetRag.Rag.Services;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotnetRag.Tests.Unit.Services;

public sealed class FilterExpressionServiceTests
{
    private static FilterExpressionService Build(
        IChatCompletionService chat,
        IVectorStore? store = null,
        bool enabled = true,
        string vectorStore = "milvus")
    {
        var mockStore = store ?? new Mock<IVectorStore>().Object;
        var cfg = RagServerConfiguration.FromEnvironment();
        return new FilterExpressionService(chat, mockStore, cfg, NullLogger<FilterExpressionService>.Instance);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsNull_WhenDisabledByConfig()
    {
        var chat = new Mock<IChatCompletionService>();
        // Config has ENABLE_FILTER_GENERATOR=false by default
        var svc = Build(chat.Object);

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

        var store = new Mock<IVectorStore>();
        store.Setup(s => s.GetSchemaDescriptionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("schema: field1 (str), field2 (int)");

        var svc = Build(chat.Object, store.Object);

        // Even when called directly, returns null for "None" response
        var result = await svc.GenerateAsync("hello there", "col");

        result.Should().BeNull();
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
