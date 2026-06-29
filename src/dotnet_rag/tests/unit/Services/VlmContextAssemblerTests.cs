using System.Text.Json;
using DotnetRag.Rag.Services;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Models;
using FluentAssertions;

namespace DotnetRag.Tests.Unit.Services;

public sealed class VlmContextAssemblerTests
{
    [Fact]
    public void Assemble_WithRetrievedContext_AddsContextInstructionBeforeChatHistory()
    {
        var assembler = new VlmContextAssembler();
        var content = JsonDocument.Parse("""
        [
          { "type": "text", "text": "What is shown?" },
          { "type": "image_url", "image_url": { "url": "data:image/png;base64,abc" } }
        ]
        """).RootElement.Clone();

        var messages = assembler.Assemble(new VlmContextAssemblyRequest(
            [new Message("user", content)],
            [
                new VectorSearchResult("chunk-1", "Revenue was 42.", 0.9, new Dictionary<string, string>
                {
                    ["filename"] = "report.pdf"
                })
            ],
            [],
            "system prompt",
            "Context:\n{context}\n\nQuestion:\n{question}",
            MaxTotalImages: 3,
            IncludeSourceMetadata: true));

        messages.Should().HaveCount(3);
        messages[0].Role.Should().Be("system");
        messages[0].Content.Should().Be("system prompt");
        messages[1].Role.Should().Be("user");
        messages[1].Content.ToString().Should().Contain("[Source: report.pdf]");
        messages[1].Content.ToString().Should().Contain("Revenue was 42.");
        messages[1].Content.ToString().Should().Contain("What is shown?");
        JsonSerializer.Serialize(messages[2].Content).Should().Contain("image_url");
    }

    [Fact]
    public void Assemble_PreservesUserImagesWhenBudgetIsExhausted()
    {
        var assembler = new VlmContextAssembler();
        var content = JsonDocument.Parse("""
        [
          { "type": "text", "text": "Compare these." },
          { "type": "image_url", "image_url": { "url": "data:image/png;base64,one" } },
          { "type": "image_url", "image_url": { "url": "data:image/png;base64,two" } }
        ]
        """).RootElement.Clone();

        var messages = assembler.Assemble(new VlmContextAssemblyRequest(
            [new Message("user", content)],
            [],
            [],
            "system prompt",
            "{context}\n\n{question}",
            MaxTotalImages: 1,
            IncludeSourceMetadata: false));

        var serialized = JsonSerializer.Serialize(messages[1].Content);
        serialized.Should().Contain("data:image/png;base64,one");
        serialized.Should().Contain("data:image/png;base64,two");
    }

    [Fact]
    public void Assemble_AddsResolvedContextImagesWithinRemainingBudget()
    {
        var assembler = new VlmContextAssembler();
        var content = JsonDocument.Parse("""
        [
          { "type": "text", "text": "Compare these." },
          { "type": "image_url", "image_url": { "url": "data:image/png;base64,user" } }
        ]
        """).RootElement.Clone();

        var messages = assembler.Assemble(new VlmContextAssemblyRequest(
            [new Message("user", content)],
            [
                new VectorSearchResult("chunk-1", "Chart caption", 0.9, new Dictionary<string, string>
                {
                    ["filename"] = "report.pdf"
                })
            ],
            [
                new VlmContextAsset("asset-one", "image", "report.pdf", 2, "Chart caption"),
                new VlmContextAsset("asset-two", "image", "report.pdf", 3, "Second chart")
            ],
            "system prompt",
            "Context:\n{context}\n\nQuestion:\n{question}",
            MaxTotalImages: 2,
            IncludeSourceMetadata: true));

        messages.Should().HaveCount(3);
        var contextMessage = JsonSerializer.Serialize(messages[1].Content);
        contextMessage.Should().Contain("data:image/png;base64,asset-one");
        contextMessage.Should().Contain("=== Page 2 (report) ===");
        contextMessage.Should().Contain("Chart caption");
        contextMessage.Should().NotContain("data:image/png;base64,asset-two");

        var userMessage = JsonSerializer.Serialize(messages[2].Content);
        userMessage.Should().Contain("data:image/png;base64,user");
    }

    [Fact]
    public void Assemble_OrdersResolvedContextImagesBySourceThenPage()
    {
        var assembler = new VlmContextAssembler();

        var messages = assembler.Assemble(new VlmContextAssemblyRequest(
            [new Message("user", "What changed?")],
            [
                new VectorSearchResult("chunk-1", "Context", 0.9, new Dictionary<string, string>
                {
                    ["filename"] = "report.pdf"
                })
            ],
            [
                new VlmContextAsset("asset-page-3", "image", "/docs/b-report.pdf", 3, "B page 3"),
                new VlmContextAsset("asset-page-2", "image", "/docs/a-report.pdf", 2, "A page 2"),
                new VlmContextAsset("asset-page-1", "image", "/docs/a-report.pdf", 1, "A page 1"),
                new VlmContextAsset("asset-no-page", "image", "/docs/a-report.pdf", null, "No page")
            ],
            "system prompt",
            "Context:\n{context}\n\nQuestion:\n{question}",
            MaxTotalImages: 4,
            IncludeSourceMetadata: true));

        var contextMessage = JsonSerializer.Serialize(messages[1].Content);
        contextMessage.IndexOf("=== Page 1 (a-report) ===", StringComparison.Ordinal)
            .Should().BeLessThan(contextMessage.IndexOf("=== Page 2 (a-report) ===", StringComparison.Ordinal));
        contextMessage.IndexOf("=== Page 2 (a-report) ===", StringComparison.Ordinal)
            .Should().BeLessThan(contextMessage.IndexOf("=== Page 3 (b-report) ===", StringComparison.Ordinal));
        contextMessage.IndexOf("=== Page 3 (b-report) ===", StringComparison.Ordinal)
            .Should().BeLessThan(contextMessage.IndexOf("Retrieved visual context (/docs/a-report.pdf):", StringComparison.Ordinal));
        contextMessage.Should().Contain("data:image/png;base64,asset-page-1");
        contextMessage.Should().Contain("data:image/png;base64,asset-page-2");
        contextMessage.Should().Contain("data:image/png;base64,asset-page-3");
        contextMessage.Should().Contain("data:image/png;base64,asset-no-page");
    }
}
