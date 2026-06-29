using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DotnetRag.Ingestor.Services;
using DotnetRag.Rag.Clients;
using DotnetRag.Rag.Observability;
using DotnetRag.Rag.Services;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Models;
using DotnetRag.Shared.Prompts;
using DotnetRag.Shared.Summarization;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotnetRag.Tests.Unit.Services;

public sealed class RagServicePromptBehaviorTests
{
    [Fact]
    public async Task GenerateAsync_NoKnowledgeBase_UsesChatTemplate()
    {
        ChatCompletionRequest? captured = null;
        var chat = MockChat(request => captured = request);
        var service = BuildService(chat.Object);
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt([new Message("user", "Hello")], UseKnowledgeBase: false);

        var result = await service.GenerateAsync(context.Request, prompt);

        captured.Should().NotBeNull();
        captured!.Messages.Should().ContainSingle(m =>
            m.Role == "system"
            && m.Content.ToString()!.Contains("You are a helpful, respectful, and honest assistant."));
        var valueResult = result.Should().BeAssignableTo<IValueHttpResult>().Subject;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(valueResult.Value));
        json.RootElement.TryGetProperty("citations", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAsync_NonStreaming_RecordsPromptModelAndUsageSpanTags()
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
                "answer",
                Usage: new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = 7,
                    ["completion_tokens"] = 3,
                    ["total_tokens"] = 10
                }));
        var service = BuildService(chat.Object);
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [new Message("user", "Hello")],
            UseKnowledgeBase: false,
            Model: "trace-model");

        await service.GenerateAsync(context.Request, prompt);

        var activity = completed.Single(a => a.DisplayName == "rag.generate");
        activity.GetTagItem("rag.request.path").Should().Be("/chat/completions");
        activity.GetTagItem("rag.prompt.template").Should().Be("chat_template");
        activity.GetTagItem("rag.prompt.message_count").Should().Be(1);
        activity.GetTagItem("rag.knowledge_base.enabled").Should().Be(false);
        activity.GetTagItem("gen_ai.request.model").Should().Be("trace-model");
        activity.GetTagItem("gen_ai.usage.input_tokens").Should().Be(7);
        activity.GetTagItem("gen_ai.usage.output_tokens").Should().Be(3);
        activity.GetTagItem("llm.usage.total_tokens").Should().Be(10);
    }

    [Fact]
    public async Task GenerateAsync_WithThinkingTokenControls_PassesReasoningOptionsToLlm()
    {
        ChatCompletionRequest? captured = null;
        var chat = MockChat(request => captured = request);
        var service = BuildService(chat.Object);
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [new Message("user", "Think carefully")],
            UseKnowledgeBase: false,
            MinThinkingTokens: 16,
            MaxThinkingTokens: 256);

        await service.GenerateAsync(context.Request, prompt);

        captured.Should().NotBeNull();
        captured!.EnableThinking.Should().BeTrue();
        captured.ThinkingTokenBudget.Should().Be(256);
    }

    [Fact]
    public async Task GenerateAsync_WithKnowledgeBase_UsesRagTemplate()
    {
        ChatCompletionRequest? captured = null;
        var chat = MockChat(request => captured = request);
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs", "What is revenue?", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-1", "Revenue was $1.", 0.95, new Dictionary<string, string>
                {
                    ["filename"] = "report.pdf"
                })
            ]);

        var service = BuildService(chat.Object, vectorStore: vectorStore.Object);
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [new Message("user", "What is revenue?")],
            CollectionNames: ["docs"],
            EnableReranker: false,
            ConfidenceThreshold: 0.0);

        await service.GenerateAsync(context.Request, prompt);

        captured.Should().NotBeNull();
        captured!.Messages.Should().Contain(m =>
            m.Role == "system"
            && m.Content.ToString()!.Contains("Envie"));
        captured.Messages.Should().Contain(m =>
            m.Role == "user"
            && m.Content.ToString()!.Contains("<context>")
            && m.Content.ToString()!.Contains("Revenue was $1."));
    }

    [Fact]
    public async Task GenerateAsync_WithMultipleCollections_SearchesAndMergesContexts()
    {
        ChatCompletionRequest? captured = null;
        var chat = MockChat(request => captured = request);
        var searchedCollections = new List<string>();
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string collection, string _, int _, string? _, CancellationToken _) =>
            {
                searchedCollections.Add(collection);
                return
                [
                    new VectorSearchResult($"{collection}-chunk", $"Context from {collection}", 0.95)
                ];
            });

        var service = BuildService(
            chat.Object,
            vectorStore: vectorStore.Object,
            queryRewriterModel: "query-decomp-model");
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [new Message("user", "What changed?")],
            CollectionNames: ["docs-a", "docs-b"],
            EnableReranker: false,
            ConfidenceThreshold: 0.0);

        await service.GenerateAsync(context.Request, prompt);

        searchedCollections.Should().BeEquivalentTo(["docs-a", "docs-b"]);
        captured.Should().NotBeNull();
        captured!.Messages.Should().Contain(m =>
            m.Role == "user"
            && m.Content.ToString()!.Contains("Context from docs-a")
            && m.Content.ToString()!.Contains("Context from docs-b"));
    }

    [Fact]
    public async Task GenerateAsync_WithVdbEndpoint_UsesRequestScopedVectorStore()
    {
        var chat = MockChat(_ => { });
        var defaultStore = new Mock<IVectorStore>();
        var requestStore = new Mock<IVectorStore>();
        requestStore.Setup(s => s.SearchAsync(
                "docs", "Where is this stored?", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("request-chunk", "Request scoped context", 0.95)
            ]);
        var factory = new Mock<IVectorStoreClientFactory>();
        factory.Setup(f => f.Create(
                "http://override-vdb:8000",
                "runtime-token",
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .Returns(new VectorStoreClient(
                requestStore.Object,
                Mock.Of<IVectorStoreManagement>(),
                Mock.Of<IVectorStoreFilterCapabilities>()));

        var service = BuildService(
            chat.Object,
            vectorStore: defaultStore.Object,
            vectorStoreClientFactory: factory.Object);
        var context = BuildContext("/chat/completions");
        context.Request.Headers.Authorization = "Bearer runtime-token";
        var prompt = new Prompt(
            [new Message("user", "Where is this stored?")],
            CollectionNames: ["docs"],
            VdbEndpoint: "http://override-vdb:8000",
            EnableReranker: false,
            ConfidenceThreshold: 0.0);

        await service.GenerateAsync(context.Request, prompt);

        requestStore.Verify(s => s.SearchAsync(
            "docs", "Where is this stored?", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        defaultStore.Verify(s => s.SearchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_WithEmbeddingEndpoint_UsesRequestScopedVectorStore()
    {
        var chat = MockChat(_ => { });
        var defaultStore = new Mock<IVectorStore>();
        var requestStore = new Mock<IVectorStore>();
        requestStore.Setup(s => s.SearchAsync(
                "docs", "Embed with override", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("request-chunk", "Request scoped embedding context", 0.95)
            ]);
        var factory = new Mock<IVectorStoreClientFactory>();
        factory.Setup(f => f.Create(
                null,
                null,
                "http://override-embed:8000/v1",
                "override-embed-model"))
            .Returns(new VectorStoreClient(
                requestStore.Object,
                Mock.Of<IVectorStoreManagement>(),
                Mock.Of<IVectorStoreFilterCapabilities>()));

        var service = BuildService(
            chat.Object,
            vectorStore: defaultStore.Object,
            vectorStoreClientFactory: factory.Object);
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [new Message("user", "Embed with override")],
            CollectionNames: ["docs"],
            EmbeddingEndpoint: "http://override-embed:8000/v1",
            EmbeddingModel: "override-embed-model",
            EnableReranker: false,
            ConfidenceThreshold: 0.0);

        await service.GenerateAsync(context.Request, prompt);

        requestStore.Verify(s => s.SearchAsync(
            "docs", "Embed with override", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        defaultStore.Verify(s => s.SearchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_WithQueryRewriterOverrides_UsesRequestScopedRoleClient()
    {
        ChatCompletionRequest? roleRequest = null;
        var roleChat = new Mock<IChatCompletionService>();
        roleChat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((request, _) => roleRequest = request)
            .ReturnsAsync(new ChatCompletionResponse("rewritten query", null));

        var answerChat = MockChat(_ => { });
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs", "rewritten query", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-1", "Context", 0.95)
            ]);

        var chatFactory = new Mock<IChatCompletionClientFactory>();
        chatFactory.Setup(f => f.Create(
                "openai",
                "request-query-model",
                "http://role-query/v1",
                "role-key"))
            .Returns(roleChat.Object);

        var service = BuildService(
            answerChat.Object,
            vectorStore: vectorStore.Object,
            chatCompletionClientFactory: chatFactory.Object,
            conversationHistory: 2);
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [
                new Message("user", "Tell me about revenue"),
                new Message("assistant", "Revenue was discussed."),
                new Message("user", "What changed?")
            ],
            CollectionNames: ["docs"],
            EnableQueryRewriting: true,
            EnableReranker: false,
            ConfidenceThreshold: 0.0,
            QueryRewriterModel: "request-query-model",
            QueryRewriterEndpoint: "http://role-query/v1",
            QueryRewriterApiKey: "role-key");

        await service.GenerateAsync(context.Request, prompt);

        roleRequest.Should().NotBeNull();
        roleRequest!.Model.Should().Be("request-query-model");
        vectorStore.Verify(s => s.SearchAsync(
            "docs", "rewritten query", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        chatFactory.VerifyAll();
    }

    [Fact]
    public async Task GenerateAsync_WithLlmEndpoint_UsesRequestScopedChatClient()
    {
        var defaultChat = MockChat(_ => { });
        var overrideChat = MockChat(_ => { });
        var factory = new Mock<IChatCompletionClientFactory>();
        factory.Setup(f => f.Create(
                "openai",
                "override-model",
                "http://override-llm:8000/v1",
                It.IsAny<string?>()))
            .Returns(overrideChat.Object);

        var service = BuildService(defaultChat.Object, chatCompletionClientFactory: factory.Object);
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [new Message("user", "Hello")],
            UseKnowledgeBase: false,
            Model: "override-model",
            LlmEndpoint: "http://override-llm:8000/v1");

        await service.GenerateAsync(context.Request, prompt);

        overrideChat.Verify(c => c.CompleteAsync(
            It.Is<ChatCompletionRequest>(r => r.Model == "override-model"),
            It.IsAny<CancellationToken>()), Times.Once);
        defaultChat.Verify(c => c.CompleteAsync(
            It.IsAny<ChatCompletionRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_WithRerankerEndpoint_PassesEndpointToReranker()
    {
        var chat = MockChat(_ => { });
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs", "Rank these", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-1", "First context", 0.90),
                new VectorSearchResult("chunk-2", "Second context", 0.89)
            ]);
        var reranker = new Mock<IRerankerClient>();
        reranker.Setup(r => r.RerankAsync(
                "Rank these",
                It.IsAny<IReadOnlyList<VectorSearchResult>>(),
                10,
                It.IsAny<CancellationToken>(),
                "http://override-reranker:8083"))
            .ReturnsAsync((string _, IReadOnlyList<VectorSearchResult> candidates, int _, CancellationToken _, string? _) =>
                candidates);

        var service = BuildService(
            chat.Object,
            vectorStore: vectorStore.Object,
            reranker: reranker.Object,
            enableReranker: true);
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [new Message("user", "Rank these")],
            CollectionNames: ["docs"],
            EnableReranker: true,
            RerankerEndpoint: "http://override-reranker:8083",
            ConfidenceThreshold: 0.0);

        await service.GenerateAsync(context.Request, prompt);

        reranker.Verify(r => r.RerankAsync(
            "Rank these",
            It.Is<IReadOnlyList<VectorSearchResult>>(items => items.Count == 2),
            10,
            It.IsAny<CancellationToken>(),
            "http://override-reranker:8083"), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_WithConfidenceThreshold_FiltersAfterReranking()
    {
        ChatCompletionRequest? captured = null;
        var chat = MockChat(request => captured = request);
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs", "Threshold test", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-low-vector", "Kept after rerank", 0.10),
                new VectorSearchResult("chunk-drop", "Dropped after rerank", 0.90)
            ]);
        var reranker = new Mock<IRerankerClient>();
        reranker.Setup(r => r.RerankAsync(
                "Threshold test",
                It.IsAny<IReadOnlyList<VectorSearchResult>>(),
                10,
                It.IsAny<CancellationToken>(),
                null))
            .ReturnsAsync([
                new VectorSearchResult("chunk-low-vector", "Kept after rerank", 0.95),
                new VectorSearchResult("chunk-drop", "Dropped after rerank", 0.30)
            ]);

        var service = BuildService(
            chat.Object,
            vectorStore: vectorStore.Object,
            reranker: reranker.Object,
            enableReranker: true);
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [new Message("user", "Threshold test")],
            CollectionNames: ["docs"],
            EnableReranker: true,
            ConfidenceThreshold: 0.80);

        await service.GenerateAsync(context.Request, prompt);

        reranker.Verify(r => r.RerankAsync(
            "Threshold test",
            It.Is<IReadOnlyList<VectorSearchResult>>(items =>
                items.Any(item => item.Id == "chunk-low-vector" && item.Score == 0.10)),
            10,
            It.IsAny<CancellationToken>(),
            null), Times.Once);
        captured.Should().NotBeNull();
        var ragPrompt = string.Join("\n", captured!.Messages.Select(m => m.Content.ToString()));
        ragPrompt.Should().Contain("Kept after rerank");
        ragPrompt.Should().NotContain("Dropped after rerank");
    }

    [Fact]
    public async Task SearchAsync_WhenRerankerUnavailable_ReturnsBackendFailure()
    {
        var chat = new Mock<IChatCompletionService>();
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs", "Rank these", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-1", "First context", 0.90),
                new VectorSearchResult("chunk-2", "Second context", 0.89)
            ]);
        var reranker = new Mock<IRerankerClient>();
        reranker.Setup(r => r.RerankAsync(
                "Rank these",
                It.IsAny<IReadOnlyList<VectorSearchResult>>(),
                10,
                It.IsAny<CancellationToken>(),
                "http://127.0.0.1:9"))
            .ThrowsAsync(new HttpRequestException(
                "Reranker NIM unavailable at http://127.0.0.1:9. Please verify the service is running and accessible."));

        var service = BuildService(
            chat.Object,
            vectorStore: vectorStore.Object,
            reranker: reranker.Object,
            enableReranker: true);
        var context = BuildContext("/search");
        var search = new DocumentSearch(
            Query: "Rank these",
            CollectionNames: ["docs"],
            EnableReranker: true,
            RerankerEndpoint: "http://127.0.0.1:9");

        var result = await service.SearchAsync(context.Request, search);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        var valueResult = result.Should().BeAssignableTo<IValueHttpResult>().Subject;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(valueResult.Value));
        json.RootElement.GetProperty("message").GetString()
            .Should().Contain("Reranker NIM unavailable");
    }

    [Fact]
    public async Task GenerateAsync_WithConfidenceThresholdAndRerankerDisabled_DoesNotFilterVectorScores()
    {
        ChatCompletionRequest? captured = null;
        var chat = MockChat(request => captured = request);
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs", "Threshold without reranker", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-low-vector", "Low vector score context", 0.10)
            ]);

        var service = BuildService(
            chat.Object,
            vectorStore: vectorStore.Object,
            enableReranker: false);
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [new Message("user", "Threshold without reranker")],
            CollectionNames: ["docs"],
            EnableReranker: false,
            ConfidenceThreshold: 0.80);

        await service.GenerateAsync(context.Request, prompt);

        captured.Should().NotBeNull();
        string.Join("\n", captured!.Messages.Select(m => m.Content.ToString()))
            .Should().Contain("Low vector score context");
    }

    [Fact]
    public async Task VectorStoreSearchAsync_WithRerankerEndpoint_PassesEndpointToReranker()
    {
        var chat = MockChat(_ => { });
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs", "Find relevant chunks", 5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-1", "First context", 0.90, new Dictionary<string, string>
                {
                    ["filename"] = "source.pdf",
                    ["type"] = "pdf"
                }),
                new VectorSearchResult("chunk-2", "Second context", 0.89)
            ]);
        var reranker = new Mock<IRerankerClient>();
        reranker.Setup(r => r.RerankAsync(
                "Find relevant chunks",
                It.IsAny<IReadOnlyList<VectorSearchResult>>(),
                5,
                It.IsAny<CancellationToken>(),
                "http://override-reranker:8083"))
            .ReturnsAsync((string _, IReadOnlyList<VectorSearchResult> candidates, int _, CancellationToken _, string? _) =>
                candidates);

        var service = BuildService(
            chat.Object,
            vectorStore: vectorStore.Object,
            reranker: reranker.Object,
            enableReranker: true);
        var context = BuildContext("/v2/vector_stores/docs/search");

        var result = await service.VectorStoreSearchAsync(
            context.Request,
            "docs",
            new VectorStoreSearchRequest(
                Query: "Find relevant chunks",
                MaxNumResults: 5,
                RerankerEndpoint: "http://override-reranker:8083"));

        reranker.Verify(r => r.RerankAsync(
            "Find relevant chunks",
            It.Is<IReadOnlyList<VectorSearchResult>>(items => items.Count == 2),
            5,
            It.IsAny<CancellationToken>(),
            "http://override-reranker:8083"), Times.Once);

        var ok = result.Should().BeOfType<Ok<VectorStoreSearchResponse>>().Subject;
        var item = ok.Value!.Data[0];
        item.Filename.Should().Be("source.pdf");
        item.Content.Should().ContainSingle(c => c.Type == "text" && c.Text == "First context");
        item.Attributes.Should().ContainKey("document_id").WhoseValue.Should().Be("chunk-1");
        item.Attributes.Should().ContainKey("content").WhoseValue.Should().Be("First context");
        item.Attributes.Should().ContainKey("text").WhoseValue.Should().Be("First context");
        item.Attributes.Should().ContainKey("source").WhoseValue.Should().Be("source.pdf");
        item.Attributes.Should().ContainKey("document_name").WhoseValue.Should().Be("source.pdf");
        item.Attributes.Should().ContainKey("collection_name").WhoseValue.Should().Be("docs");
        item.Attributes.Should().ContainKey("document_type").WhoseValue.Should().Be("pdf");
        item.Attributes.Should().ContainKey("score").WhoseValue.Should().Be(0.90);
    }

    [Fact]
    public async Task VectorStoreSearchAsync_WithRankerNone_DoesNotCallReranker()
    {
        var chat = MockChat(_ => { });
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs", "Find relevant chunks", 5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-1", "First context", 0.90)
            ]);
        var reranker = new Mock<IRerankerClient>();

        var service = BuildService(
            chat.Object,
            vectorStore: vectorStore.Object,
            reranker: reranker.Object,
            enableReranker: true);
        var context = BuildContext("/v2/vector_stores/docs/search");

        await service.VectorStoreSearchAsync(
            context.Request,
            "docs",
            new VectorStoreSearchRequest(
                Query: "Find relevant chunks",
                MaxNumResults: 5,
                RankingOptions: new RankingOptions(Ranker: "none")));

        reranker.Verify(r => r.RerankAsync(
            It.IsAny<string>(),
            It.IsAny<IReadOnlyList<VectorSearchResult>>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task VectorStoreSearchAsync_WithVdbAndEmbeddingOverrides_UsesRequestScopedVectorStore()
    {
        var chat = MockChat(_ => { });
        var defaultStore = new Mock<IVectorStore>();
        var requestStore = new Mock<IVectorStore>();
        requestStore.Setup(s => s.SearchAsync(
                "docs",
                "Find runtime chunks",
                5,
                """content_metadata["year"] == 2024""",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-1", "Runtime context", 0.90)
            ]);
        var factory = new Mock<IVectorStoreClientFactory>();
        factory.Setup(f => f.Create(
                "http://override-vdb:8000",
                "runtime-token",
                "http://override-embed:8000/v1",
                "override-embed-model"))
            .Returns(new VectorStoreClient(
                requestStore.Object,
                Mock.Of<IVectorStoreManagement>(),
                Mock.Of<IVectorStoreFilterCapabilities>()));

        var service = BuildService(
            chat.Object,
            vectorStore: defaultStore.Object,
            vectorStoreClientFactory: factory.Object);
        var context = BuildContext("/v2/vector_stores/docs/search");
        context.Request.Headers.Authorization = "Bearer runtime-token";

        await service.VectorStoreSearchAsync(
            context.Request,
            "docs",
            new VectorStoreSearchRequest(
                Query: "Find runtime chunks",
                Filters: new ComparisonFilter("year", "eq", 2024),
                MaxNumResults: 5,
                VdbEndpoint: "http://override-vdb:8000",
                EmbeddingEndpoint: "http://override-embed:8000/v1",
                EmbeddingModel: "override-embed-model"));

        requestStore.Verify(s => s.SearchAsync(
            "docs",
            "Find runtime chunks",
            5,
            """content_metadata["year"] == 2024""",
            It.IsAny<CancellationToken>()), Times.Once);
        defaultStore.Verify(s => s.SearchAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VectorStoreSearchAsync_WithRewriteQuery_UsesRequestScopedQueryRewriter()
    {
        ChatCompletionRequest? roleRequest = null;
        var roleChat = new Mock<IChatCompletionService>();
        roleChat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((request, _) => roleRequest = request)
            .ReturnsAsync(new ChatCompletionResponse("rewritten vector query", null));

        var answerChat = MockChat(_ => { });
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs",
                "rewritten vector query",
                5,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-1", "Context", 0.90)
            ]);
        var chatFactory = new Mock<IChatCompletionClientFactory>();
        chatFactory.Setup(f => f.Create(
                "openai",
                "vector-query-model",
                "http://role-query/v1",
                "role-key"))
            .Returns(roleChat.Object);

        var service = BuildService(
            answerChat.Object,
            vectorStore: vectorStore.Object,
            chatCompletionClientFactory: chatFactory.Object,
            conversationHistory: 1);
        var context = BuildContext("/v2/vector_stores/docs/search");

        await service.VectorStoreSearchAsync(
            context.Request,
            "docs",
            new VectorStoreSearchRequest(
                Query: "Find chunks",
                MaxNumResults: 5,
                RewriteQuery: true,
                QueryRewriterModel: "vector-query-model",
                QueryRewriterEndpoint: "http://role-query/v1",
                QueryRewriterApiKey: "role-key"));

        roleRequest.Should().NotBeNull();
        roleRequest!.Model.Should().Be("vector-query-model");
        vectorStore.Verify(s => s.SearchAsync(
            "docs",
            "rewritten vector query",
            5,
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        chatFactory.VerifyAll();
    }

    [Fact]
    public void VectorStoreSearchRequest_DeserializesOpenAiFilterShape()
    {
        var request = JsonSerializer.Deserialize<VectorStoreSearchRequest>(
            """
            {
              "query": "Find reports",
              "filters": {
                "type": "and",
                "filters": [
                  { "type": "eq", "key": "year", "value": 2024 },
                  { "type": "eq", "key": "is_public", "value": true }
                ]
              }
            }
            """,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        request.Should().NotBeNull();
        var compound = request!.Filters.Should().BeOfType<CompoundFilter>().Subject;
        compound.Type.Should().Be("and");
        compound.Filters.Should().HaveCount(2);
        var first = compound.Filters[0].Should().BeOfType<ComparisonFilter>().Subject;
        first.Key.Should().Be("year");
        first.Type.Should().Be("eq");
        ((JsonElement)first.Value).GetInt32().Should().Be(2024);
    }

    [Fact]
    public async Task GenerateAsync_StreamingCitations_IncludeCollectionAndContentFields()
    {
        var chat = MockChat(_ => { });
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs", "Where is this from?", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-1", "Citation body", 0.91, new Dictionary<string, string>
                {
                    ["filename"] = "source.pdf"
                })
            ]);

        var service = BuildService(
            chat.Object,
            vectorStore: vectorStore.Object,
            queryRewriterModel: "query-decomp-model");
        var context = BuildContext("/generate");
        var prompt = new Prompt(
            [new Message("user", "Where is this from?")],
            CollectionNames: ["docs"],
            EnableReranker: false,
            ConfidenceThreshold: 0.0);

        await service.GenerateAsync(context.Request, prompt);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var finalEvent = body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"finish_reason\":\"stop\"", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(finalEvent["data: ".Length..]);
        var citations = json.RootElement.GetProperty("citations");
        citations.GetProperty("total_results").GetInt32().Should().Be(1);
        var citation = citations.GetProperty("results")[0];
        citation.GetProperty("document_id").GetString().Should().Be("chunk-1");
        citation.GetProperty("content").GetString().Should().Be("Citation body");
        citation.GetProperty("text").GetString().Should().Be("Citation body");
        citation.GetProperty("source").GetString().Should().Be("source.pdf");
        citation.GetProperty("document_name").GetString().Should().Be("source.pdf");
        citation.GetProperty("collection_name").GetString().Should().Be("docs");
        citation.GetProperty("score").GetDouble().Should().BeApproximately(0.91, 0.0001);
    }

    [Fact]
    public async Task GenerateAsync_StreamingWithoutBuffering_ForwardsProviderDeltas()
    {
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse("unused", null));
        chat.Setup(c => c.StreamDeltasAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Returns((ChatCompletionRequest _, CancellationToken cancellationToken) =>
                ToAsyncDeltaEnumerable(
                    [
                        new ChatStreamDelta(Content: "alpha"),
                        new ChatStreamDelta(Content: "beta"),
                        new ChatStreamDelta(Usage: new Dictionary<string, object?>
                        {
                            ["prompt_tokens"] = 4,
                            ["completion_tokens"] = 5,
                            ["total_tokens"] = 9
                        })
                    ],
                    cancellationToken));

        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs", "Stream directly?", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-1", "Citation body", 0.91)
            ]);

        var service = BuildService(
            chat.Object,
            vectorStore: vectorStore.Object,
            filterThinkTokens: false);
        var context = BuildContext("/generate");
        var prompt = new Prompt(
            [new Message("user", "Stream directly?")],
            CollectionNames: ["docs"],
            EnableReranker: false,
            EnableGuardrails: false,
            ConfidenceThreshold: 0.0);

        await service.GenerateAsync(context.Request, prompt);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var events = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        events.Should().Contain(line => line.Contains("\"content\":\"alpha\"", StringComparison.Ordinal));
        events.Should().Contain(line => line.Contains("\"content\":\"beta\"", StringComparison.Ordinal));
        events.Should().NotContain(line => line.Contains("\"content\":\"alphabeta\"", StringComparison.Ordinal));
        var finalEvent = events.Single(line => line.Contains("\"finish_reason\":\"stop\"", StringComparison.Ordinal));
        var finalJson = finalEvent["data: ".Length..];
        using var finalDocument = JsonDocument.Parse(finalJson);
        finalDocument.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32().Should().Be(4);
        finalDocument.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32().Should().Be(5);
        finalDocument.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32().Should().Be(9);
        chat.Verify(c => c.StreamDeltasAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_StreamingWithThinkFiltering_StripsSplitThinkTokensWithoutBufferingAnswer()
    {
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse("unused", null));
        chat.Setup(c => c.StreamDeltasAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Returns((ChatCompletionRequest _, CancellationToken cancellationToken) =>
                ToAsyncDeltaEnumerable(
                    [
                        new ChatStreamDelta(Content: "alpha "),
                        new ChatStreamDelta(Content: "<thi"),
                        new ChatStreamDelta(Content: "nk>hidden"),
                        new ChatStreamDelta(Content: " thought</thi"),
                        new ChatStreamDelta(Content: "nk> beta"),
                        new ChatStreamDelta(ReasoningContent: "separate reasoning"),
                        new ChatStreamDelta(Usage: new Dictionary<string, object?>
                        {
                            ["prompt_tokens"] = 6,
                            ["completion_tokens"] = 7,
                            ["total_tokens"] = 13
                        })
                    ],
                    cancellationToken));

        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs", "Stream with hidden thinking?", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-1", "Citation body", 0.91)
            ]);

        var service = BuildService(
            chat.Object,
            vectorStore: vectorStore.Object,
            filterThinkTokens: true);
        var context = BuildContext("/generate");
        var prompt = new Prompt(
            [new Message("user", "Stream with hidden thinking?")],
            CollectionNames: ["docs"],
            EnableReranker: false,
            EnableGuardrails: false,
            ConfidenceThreshold: 0.0);

        await service.GenerateAsync(context.Request, prompt);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var compact = body.Replace(" ", string.Empty, StringComparison.Ordinal);
        var events = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        events.Should().Contain(line => line.Contains("\"content\":\"alph\"", StringComparison.Ordinal));
        events.Should().Contain(line => line.Contains("\"content\":\"a \"", StringComparison.Ordinal));
        events.Should().Contain(line => line.Contains("\"content\":\" beta\"", StringComparison.Ordinal));
        events.Should().NotContain(line => line.Contains("\"content\":\"alpha  beta\"", StringComparison.Ordinal));
        compact.Should().NotContain("hidden");
        compact.Should().NotContain("<think>");
        compact.Should().NotContain("</think>");
        body.Should().Contain("\"reasoning_content\":\"separate reasoning\"");
        var finalEvent = events.Single(line => line.Contains("\"finish_reason\":\"stop\"", StringComparison.Ordinal));
        using var finalDocument = JsonDocument.Parse(finalEvent["data: ".Length..]);
        finalDocument.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32().Should().Be(6);
        finalDocument.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32().Should().Be(7);
        finalDocument.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32().Should().Be(13);
        chat.Verify(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        chat.Verify(c => c.StreamDeltasAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_NonStreamingCitations_IncludeCollectionAndContentFields()
    {
        var chat = MockChat(_ => { });
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs", "Where is this from?", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-1", "Citation body", 0.91, new Dictionary<string, string>
                {
                    ["filename"] = "source.pdf",
                    ["type"] = "text",
                    ["content_metadata.page_number"] = "7",
                    ["content_metadata.location"] = "[1.0,2.0,3.0,4.0]",
                    ["source_location"] = "s3://assets/source.pdf"
                })
            ]);

        var service = BuildService(
            chat.Object,
            vectorStore: vectorStore.Object,
            queryRewriterModel: "query-decomp-model");
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [new Message("user", "Where is this from?")],
            CollectionNames: ["docs"],
            EnableReranker: false,
            ConfidenceThreshold: 0.0);

        var result = await service.GenerateAsync(context.Request, prompt);

        var valueResult = result.Should().BeAssignableTo<IValueHttpResult>().Subject;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(valueResult.Value));
        var citations = json.RootElement.GetProperty("citations");
        citations.GetProperty("total_results").GetInt32().Should().Be(1);
        var citation = citations.GetProperty("results")[0];
        citation.GetProperty("document_id").GetString().Should().Be("chunk-1");
        citation.GetProperty("content").GetString().Should().Be("Citation body");
        citation.GetProperty("text").GetString().Should().Be("Citation body");
        citation.GetProperty("source").GetString().Should().Be("source.pdf");
        citation.GetProperty("document_name").GetString().Should().Be("source.pdf");
        citation.GetProperty("collection_name").GetString().Should().Be("docs");
        citation.GetProperty("document_type").GetString().Should().Be("text");
        citation.GetProperty("score").GetDouble().Should().BeApproximately(0.91, 0.0001);
        var metadata = citation.GetProperty("metadata");
        metadata.GetProperty("page_number").GetInt32().Should().Be(7);
        metadata.GetProperty("source_location").GetString().Should().Be("s3://assets/source.pdf");
        metadata.GetProperty("description").GetString().Should().Be("Citation body");
        metadata.GetProperty("content_metadata").GetProperty("type").GetString().Should().Be("text");
        metadata.GetProperty("content_metadata").GetProperty("page_number").GetInt32().Should().Be(7);
        metadata.GetProperty("content_metadata").GetProperty("location")[0].GetDouble().Should().Be(1.0);
    }

    [Fact]
    public async Task GenerateAsync_NonStreamingVisualCitation_UsesResolvedAssetContent()
    {
        var chat = MockChat(_ => { });
        var vectorStore = new Mock<IVectorStore>();
        var metadata = new Dictionary<string, string>
        {
            ["filename"] = "chart.png",
            ["type"] = "image",
            ["source_location"] = "file:///tmp/chart.png"
        };
        vectorStore.Setup(s => s.SearchAsync(
                "docs", "Show the chart", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("image-1", "Chart caption", 0.88, metadata)
            ]);

        var citationAssetResolver = new Mock<ICitationAssetResolver>();
        citationAssetResolver.Setup(r => r.ResolveAsync(
                It.Is<VectorSearchResult>(result => result.Id == "image-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CitationAsset("AQIDBA==", "image"));

        var service = BuildService(
            chat.Object,
            vectorStore: vectorStore.Object,
            citationAssetResolver: citationAssetResolver.Object);
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [new Message("user", "Show the chart")],
            CollectionNames: ["docs"],
            EnableReranker: false,
            ConfidenceThreshold: 0.0);

        var result = await service.GenerateAsync(context.Request, prompt);

        var valueResult = result.Should().BeAssignableTo<IValueHttpResult>().Subject;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(valueResult.Value));
        var citation = json.RootElement.GetProperty("citations").GetProperty("results")[0];
        citation.GetProperty("content").GetString().Should().Be("AQIDBA==");
        citation.GetProperty("text").GetString().Should().Be("Chart caption");
        citation.GetProperty("document_type").GetString().Should().Be("image");
        citationAssetResolver.Verify(r => r.ResolveAsync(
            It.Is<VectorSearchResult>(result => result.Id == "image-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_VisualCitation_FromBridgeMetadata_ResolvesAssetAndPreservesProvenance()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dotnet-rag-visual-e2e-{Guid.NewGuid():N}");
        var assetPath = Path.Combine(root, "assets", "chart-1.png");
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        await File.WriteAllBytesAsync(assetPath, [1, 2, 3, 4]);

        try
        {
            var bridgeDocumentInfo = new Dictionary<string, object?>
            {
                ["document_type"] = "image",
                ["page_number"] = 2,
                ["asset_object_names"] = new List<string> { "assets/chart-1.png" },
                ["content_metadata"] = new Dictionary<string, object?>
                {
                    ["type"] = "image",
                    ["page_number"] = 2,
                    ["location"] = new List<double> { 1, 2, 3, 4 }
                }
            };
            var vectorMetadata = IngestorService.BuildVectorMetadata(
                    new Dictionary<string, object?>(),
                    bridgeDocumentInfo,
                    "deck.pdf",
                    "docs")
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value?.ToString() ?? string.Empty);

            var chat = MockChat(_ => { });
            var vectorStore = new Mock<IVectorStore>();
            vectorStore.Setup(s => s.SearchAsync(
                    "docs", "Show me the chart", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new VectorSearchResult("deck.pdf__chunk_0", "Chart caption", 0.94, vectorMetadata)
                ]);

            var resolverConfig = new RagServerConfiguration { ObjectStoreRoot = root };
            var service = BuildService(
                chat.Object,
                vectorStore: vectorStore.Object,
                citationAssetResolver: new FileSystemCitationAssetResolver(
                    resolverConfig,
                    NullLogger<FileSystemCitationAssetResolver>.Instance));
            var context = BuildContext("/chat/completions");
            var prompt = new Prompt(
                [new Message("user", "Show me the chart")],
                CollectionNames: ["docs"],
                EnableReranker: false,
                ConfidenceThreshold: 0.0);

            var result = await service.GenerateAsync(context.Request, prompt);

            var valueResult = result.Should().BeAssignableTo<IValueHttpResult>().Subject;
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(valueResult.Value));
            var citation = json.RootElement.GetProperty("citations").GetProperty("results")[0];
            citation.GetProperty("content").GetString().Should().Be("AQIDBA==");
            citation.GetProperty("text").GetString().Should().Be("Chart caption");
            citation.GetProperty("document_type").GetString().Should().Be("image");
            citation.GetProperty("source").GetString().Should().Be("deck.pdf");

            var metadata = citation.GetProperty("metadata");
            metadata.GetProperty("page_number").GetInt32().Should().Be(2);
            metadata.GetProperty("source_location").GetString().Should().Be("assets/chart-1.png");
            metadata.GetProperty("content_metadata").GetProperty("type").GetString().Should().Be("image");
            metadata.GetProperty("content_metadata").GetProperty("location")[3].GetDouble().Should().Be(4);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GenerateAsync_VisualCitation_FromNestedPythonMetadata_ResolvesAssetAndPreservesProvenance()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dotnet-rag-nested-visual-e2e-{Guid.NewGuid():N}");
        var assetPath = Path.Combine(root, "assets", "table-7.png");
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        await File.WriteAllBytesAsync(assetPath, [14, 15]);

        try
        {
            var vectorMetadata = new Dictionary<string, string>
            {
                ["filename"] = "deck.pdf",
                ["source"] = """{"source_id":"deck.pdf","source_name":"deck.pdf","source_location":"assets/table-7.png"}""",
                ["content_metadata"] = """
                {"type":"structured","subtype":"table","page_number":7,"location":[1.0,2.0,3.0,4.0]}
                """
            };
            var chat = MockChat(_ => { });
            var vectorStore = new Mock<IVectorStore>();
            vectorStore.Setup(s => s.SearchAsync(
                    "docs", "Show me the table", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new VectorSearchResult("deck.pdf__table_7", "Table caption", 0.94, vectorMetadata)
                ]);

            var service = BuildService(
                chat.Object,
                vectorStore: vectorStore.Object,
                citationAssetResolver: new FileSystemCitationAssetResolver(
                    new RagServerConfiguration { ObjectStoreRoot = root },
                    NullLogger<FileSystemCitationAssetResolver>.Instance));
            var context = BuildContext("/chat/completions");
            var prompt = new Prompt(
                [new Message("user", "Show me the table")],
                CollectionNames: ["docs"],
                EnableReranker: false,
                ConfidenceThreshold: 0.0);

            var result = await service.GenerateAsync(context.Request, prompt);

            var valueResult = result.Should().BeAssignableTo<IValueHttpResult>().Subject;
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(valueResult.Value));
            var citation = json.RootElement.GetProperty("citations").GetProperty("results")[0];
            citation.GetProperty("content").GetString().Should().Be("Dg8=");
            citation.GetProperty("text").GetString().Should().Be("Table caption");
            citation.GetProperty("document_type").GetString().Should().Be("table");
            citation.GetProperty("source").GetString().Should().Be("deck.pdf");

            var metadata = citation.GetProperty("metadata");
            metadata.GetProperty("page_number").GetInt32().Should().Be(7);
            metadata.GetProperty("source_location").GetString().Should().Be("assets/table-7.png");
            metadata.GetProperty("content_metadata").GetProperty("type").GetString().Should().Be("table");
            metadata.GetProperty("content_metadata").GetProperty("subtype").GetString().Should().Be("table");
            metadata.GetProperty("content_metadata").GetProperty("location")[3].GetDouble().Should().Be(4);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GenerateAsync_NonStreamingWithCitationsDisabled_ReturnsEmptyCitationsButKeepsContext()
    {
        ChatCompletionRequest? captured = null;
        var chat = MockChat(request => captured = request);
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs", "Where is this from?", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-1", "Citation body", 0.91, new Dictionary<string, string>
                {
                    ["filename"] = "source.pdf"
                })
            ]);

        var service = BuildService(chat.Object, vectorStore: vectorStore.Object);
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [new Message("user", "Where is this from?")],
            CollectionNames: ["docs"],
            EnableReranker: false,
            EnableCitations: false,
            ConfidenceThreshold: 0.0);

        var result = await service.GenerateAsync(context.Request, prompt);

        captured.Should().NotBeNull();
        captured!.Messages.Should().Contain(m =>
            m.Role == "user"
            && m.Content.ToString()!.Contains("Citation body"));
        var valueResult = result.Should().BeAssignableTo<IValueHttpResult>().Subject;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(valueResult.Value));
        var citations = json.RootElement.GetProperty("citations");
        citations.GetProperty("total_results").GetInt32().Should().Be(0);
        citations.GetProperty("results").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GenerateAsync_StreamingWithCitationsDisabled_ReturnsEmptyCitationsButKeepsContext()
    {
        ChatCompletionRequest? captured = null;
        var chat = MockChat(request => captured = request);
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs", "Where is this from?", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-1", "Citation body", 0.91, new Dictionary<string, string>
                {
                    ["filename"] = "source.pdf"
                })
            ]);

        var service = BuildService(chat.Object, vectorStore: vectorStore.Object);
        var context = BuildContext("/generate");
        var prompt = new Prompt(
            [new Message("user", "Where is this from?")],
            CollectionNames: ["docs"],
            EnableReranker: false,
            EnableCitations: false,
            ConfidenceThreshold: 0.0);

        await service.GenerateAsync(context.Request, prompt);

        captured.Should().NotBeNull();
        captured!.Messages.Should().Contain(m =>
            m.Role == "user"
            && m.Content.ToString()!.Contains("Citation body"));
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var finalEvent = body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"finish_reason\":\"stop\"", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(finalEvent["data: ".Length..]);
        var citations = json.RootElement.GetProperty("citations");
        citations.GetProperty("total_results").GetInt32().Should().Be(0);
        citations.GetProperty("results").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GenerateAsync_WithVlmImage_UsesVlmTemplate()
    {
        ChatCompletionRequest? captured = null;
        var mainChat = MockChat(_ => { });
        var vlmChat = MockChat(request => captured = request);
        var services = new ServiceCollection()
            .AddKeyedSingleton<IChatCompletionService>("vlm", vlmChat.Object)
            .BuildServiceProvider();
        var service = BuildService(mainChat.Object, serviceProvider: services);
        var context = BuildContext("/chat/completions");
        var content = JsonDocument.Parse("""
[
  { "type": "text", "text": "What is shown?" },
  { "type": "image_url", "image_url": { "url": "data:image/png;base64,abc" } }
]
""").RootElement.Clone();
        var prompt = new Prompt(
            [new Message("user", content)],
            UseKnowledgeBase: false,
            EnableVlmInference: true,
            VlmModel: "vlm-model",
            VlmEnableThinking: true,
            VlmThinkingTokenBudget: 321);

        await service.GenerateAsync(context.Request, prompt);

        captured.Should().NotBeNull();
        captured!.Messages[0].Role.Should().Be("system");
        captured.Messages[0].Content.ToString().Should().Contain("multimodal AI assistant");
        captured.EnableThinking.Should().BeTrue();
        captured.ThinkingTokenBudget.Should().Be(321);
    }

    [Fact]
    public async Task GenerateAsync_WithVlmImageAndKnowledgeBase_AddsRetrievedContext()
    {
        ChatCompletionRequest? captured = null;
        var mainChat = MockChat(_ => { });
        var vlmChat = MockChat(request => captured = request);
        var services = new ServiceCollection()
            .AddKeyedSingleton<IChatCompletionService>("vlm", vlmChat.Object)
            .BuildServiceProvider();
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs",
                "What is shown?",
                100,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-1", "Context from retrieved document.", 0.95, new Dictionary<string, string>
                {
                    ["filename"] = "source.pdf"
                })
            ]);

        var service = BuildService(
            mainChat.Object,
            vectorStore: vectorStore.Object,
            serviceProvider: services);
        var context = BuildContext("/chat/completions");
        var content = JsonDocument.Parse("""
[
  { "type": "text", "text": "What is shown?" },
  { "type": "image_url", "image_url": { "url": "data:image/png;base64,abc" } }
]
""").RootElement.Clone();
        var prompt = new Prompt(
            [new Message("user", content)],
            CollectionNames: ["docs"],
            EnableReranker: false,
            ConfidenceThreshold: 0.0,
            EnableVlmInference: true,
            VlmModel: "vlm-model");

        await service.GenerateAsync(context.Request, prompt);

        captured.Should().NotBeNull();
        captured!.Messages.Should().HaveCount(3);
        captured.Messages[0].Role.Should().Be("system");
        captured.Messages[1].Role.Should().Be("user");
        captured.Messages[1].Content.ToString().Should().Contain("Context from retrieved document.");
        captured.Messages[1].Content.ToString().Should().Contain("[Source: source.pdf]");
        JsonSerializer.Serialize(captured.Messages[2].Content).Should().Contain("image_url");
        vectorStore.Verify(s => s.SearchAsync(
            "docs",
            "What is shown?",
            100,
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_WithVlmImageAndVisualKnowledgeBase_AddsResolvedContextImage()
    {
        ChatCompletionRequest? captured = null;
        var mainChat = MockChat(_ => { });
        var vlmChat = MockChat(request => captured = request);
        var services = new ServiceCollection()
            .AddKeyedSingleton<IChatCompletionService>("vlm", vlmChat.Object)
            .BuildServiceProvider();
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs",
                "What is shown?",
                100,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("image-1", "Chart caption", 0.95, new Dictionary<string, string>
                {
                    ["filename"] = "chart.pdf",
                    ["type"] = "image",
                    ["page_number"] = "4",
                    ["source_location"] = "file:///tmp/chart.png"
                })
            ]);
        var citationAssetResolver = new Mock<ICitationAssetResolver>();
        citationAssetResolver.Setup(r => r.ResolveAsync(
                It.Is<VectorSearchResult>(result => result.Id == "image-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CitationAsset("AQIDBA==", "image"));

        var service = BuildService(
            mainChat.Object,
            vectorStore: vectorStore.Object,
            serviceProvider: services,
            citationAssetResolver: citationAssetResolver.Object);
        var context = BuildContext("/chat/completions");
        var content = JsonDocument.Parse("""
[
  { "type": "text", "text": "What is shown?" },
  { "type": "image_url", "image_url": { "url": "data:image/png;base64,user" } }
]
""").RootElement.Clone();
        var prompt = new Prompt(
            [new Message("user", content)],
            CollectionNames: ["docs"],
            EnableReranker: false,
            ConfidenceThreshold: 0.0,
            EnableVlmInference: true,
            VlmModel: "vlm-model",
            VlmMaxTotalImages: 2);

        await service.GenerateAsync(context.Request, prompt);

        captured.Should().NotBeNull();
        var contextMessage = JsonSerializer.Serialize(captured!.Messages[1].Content);
        contextMessage.Should().Contain("Chart caption");
        contextMessage.Should().Contain("=== Page 4 (chart) ===");
        contextMessage.Should().Contain("data:image/png;base64,AQIDBA==");
        JsonSerializer.Serialize(captured.Messages[2].Content).Should().Contain("data:image/png;base64,user");
        citationAssetResolver.Verify(r => r.ResolveAsync(
            It.Is<VectorSearchResult>(result => result.Id == "image-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_WithVlmImageAndNestedVisualKnowledgeBase_AddsResolvedContextImage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dotnet-rag-vlm-nested-{Guid.NewGuid():N}");
        var assetPath = Path.Combine(root, "assets", "page-7.png");
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        await File.WriteAllBytesAsync(assetPath, [21, 22]);

        try
        {
            ChatCompletionRequest? captured = null;
            var mainChat = MockChat(_ => { });
            var vlmChat = MockChat(request => captured = request);
            var services = new ServiceCollection()
                .AddKeyedSingleton<IChatCompletionService>("vlm", vlmChat.Object)
                .BuildServiceProvider();
            var vectorStore = new Mock<IVectorStore>();
            vectorStore.Setup(s => s.SearchAsync(
                    "docs",
                    "What is shown?",
                    100,
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new VectorSearchResult("image-7", "Nested page visual", 0.95, new Dictionary<string, string>
                    {
                        ["filename"] = "deck.pdf",
                        ["source"] = """{"source_location":"assets/page-7.png"}""",
                        ["content_metadata"] = """{"type":"image","page_number":7}"""
                    })
                ]);

            var service = BuildService(
                mainChat.Object,
                vectorStore: vectorStore.Object,
                serviceProvider: services,
                citationAssetResolver: new FileSystemCitationAssetResolver(
                    new RagServerConfiguration { ObjectStoreRoot = root },
                    NullLogger<FileSystemCitationAssetResolver>.Instance));
            var context = BuildContext("/chat/completions");
            var content = JsonDocument.Parse("""
[
  { "type": "text", "text": "What is shown?" },
  { "type": "image_url", "image_url": { "url": "data:image/png;base64,user" } }
]
""").RootElement.Clone();
            var prompt = new Prompt(
                [new Message("user", content)],
                CollectionNames: ["docs"],
                EnableReranker: false,
                ConfidenceThreshold: 0.0,
                EnableVlmInference: true,
                VlmModel: "vlm-model",
                VlmMaxTotalImages: 2);

            await service.GenerateAsync(context.Request, prompt);

            captured.Should().NotBeNull();
            var contextMessage = JsonSerializer.Serialize(captured!.Messages[1].Content);
            contextMessage.Should().Contain("Nested page visual");
            contextMessage.Should().Contain("=== Page 7 (deck) ===");
            contextMessage.Should().Contain("data:image/png;base64,FRY=");
            JsonSerializer.Serialize(captured.Messages[2].Content).Should().Contain("data:image/png;base64,user");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GenerateAsync_WithVlmImageStreaming_ReturnsEmptyCitationEnvelope()
    {
        var mainChat = MockChat(_ => { });
        var vlmChat = MockChat(_ => { });
        var services = new ServiceCollection()
            .AddKeyedSingleton<IChatCompletionService>("vlm", vlmChat.Object)
            .BuildServiceProvider();
        var service = BuildService(mainChat.Object, serviceProvider: services);
        var context = BuildContext("/generate");
        var content = JsonDocument.Parse("""
[
  { "type": "text", "text": "What is shown?" },
  { "type": "image_url", "image_url": { "url": "data:image/png;base64,abc" } }
]
""").RootElement.Clone();
        var prompt = new Prompt(
            [new Message("user", content)],
            UseKnowledgeBase: false,
            EnableVlmInference: true,
            VlmModel: "vlm-model");

        await service.GenerateAsync(context.Request, prompt);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var finalEvent = body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"finish_reason\":\"stop\"", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(finalEvent["data: ".Length..]);
        var citations = json.RootElement.GetProperty("citations");
        citations.GetProperty("total_results").GetInt32().Should().Be(0);
        citations.GetProperty("results").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GenerateAsync_WithVlmReasoningStreaming_EmitsReasoningContent()
    {
        var mainChat = MockChat(_ => { });
        var vlmChat = new Mock<IChatCompletionService>();
        vlmChat.Setup(c => c.StreamDeltasAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Returns((ChatCompletionRequest _, CancellationToken cancellationToken) =>
                ToAsyncDeltaEnumerable(
                    [
                        new ChatStreamDelta(ReasoningContent: "visual reasoning"),
                        new ChatStreamDelta(Content: "answer")
                    ],
                    cancellationToken));
        var services = new ServiceCollection()
            .AddKeyedSingleton<IChatCompletionService>("vlm", vlmChat.Object)
            .BuildServiceProvider();
        var service = BuildService(mainChat.Object, serviceProvider: services);
        var context = BuildContext("/generate");
        var content = JsonDocument.Parse("""
[
  { "type": "text", "text": "What is shown?" },
  { "type": "image_url", "image_url": { "url": "data:image/png;base64,abc" } }
]
""").RootElement.Clone();
        var prompt = new Prompt(
            [new Message("user", content)],
            UseKnowledgeBase: false,
            EnableVlmInference: true,
            VlmModel: "vlm-model",
            VlmEnableThinking: true);

        await service.GenerateAsync(context.Request, prompt);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var events = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var reasoningEvent = events.Single(line => line.Contains("reasoning_content", StringComparison.Ordinal));
        using (var reasoningJson = JsonDocument.Parse(reasoningEvent["data: ".Length..]))
        {
            reasoningJson.RootElement
                .GetProperty("choices")[0]
                .GetProperty("delta")
                .GetProperty("reasoning_content")
                .GetString()
                .Should()
                .Be("visual reasoning");
        }

        events.Should().Contain(line => line.Contains("\"content\":\"answer\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateAsync_WithQueryDecomposition_UsesDecompositionFinalPrompt()
    {
        var captured = new List<ChatCompletionRequest>();
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((request, _) => captured.Add(request))
            .ReturnsAsync((ChatCompletionRequest request, CancellationToken _) =>
            {
                var system = request.Messages[0].Content.ToString() ?? string.Empty;
                if (system.Contains("break down a user's complex question", StringComparison.OrdinalIgnoreCase))
                {
                    return new ChatCompletionResponse("1. Revenue question\n2. Margin question", null);
                }

                if (system.Contains("single standalone query", StringComparison.OrdinalIgnoreCase))
                {
                    return new ChatCompletionResponse("Rewritten margin question", null);
                }

                if (system.Contains("identifying missing information", StringComparison.OrdinalIgnoreCase))
                {
                    return new ChatCompletionResponse("", null);
                }

                if (system.Contains("Answer using ONLY the information provided", StringComparison.OrdinalIgnoreCase))
                {
                    return new ChatCompletionResponse("Sub-answer", null);
                }

                return new ChatCompletionResponse("Final answer", null);
            });

        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string query, int _, string? _, CancellationToken _) =>
            [
                new VectorSearchResult(query, $"Context for {query}", 0.95)
            ]);

        var service = BuildService(
            chat.Object,
            vectorStore: vectorStore.Object,
            queryRewriterModel: "query-decomp-model");
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [new Message("user", "Compare revenue and margin")],
            CollectionNames: ["docs"],
            EnableQueryDecomposition: true,
            EnableReranker: false,
            ConfidenceThreshold: 0.0);

        await service.GenerateAsync(context.Request, prompt);

        var finalRequest = captured.Last();
        finalRequest.Messages[0].Content.ToString().Should().Contain("sole purpose");
        finalRequest.Messages[1].Content.ToString().Should().Contain("Conversation History:");
        finalRequest.Messages[1].Content.ToString().Should().Contain("Sub-answer");
        finalRequest.Messages[1].Content.ToString().Should().Contain("Context for");
        captured.SkipLast(1).Should().OnlyContain(request => request.Model == "query-decomp-model");
        finalRequest.Model.Should().NotBe("query-decomp-model");
    }

    [Fact]
    public async Task GenerateAsync_WithQueryDecomposition_NormalizesRawRerankerScoresBeforeThreshold()
    {
        var captured = new List<ChatCompletionRequest>();
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((request, _) => captured.Add(request))
            .ReturnsAsync((ChatCompletionRequest request, CancellationToken _) =>
            {
                var system = request.Messages[0].Content.ToString() ?? string.Empty;
                if (system.Contains("break down a user's complex question", StringComparison.OrdinalIgnoreCase))
                {
                    return new ChatCompletionResponse("1. Revenue question", null);
                }

                if (system.Contains("identifying missing information", StringComparison.OrdinalIgnoreCase))
                {
                    return new ChatCompletionResponse("", null);
                }

                if (system.Contains("Answer using ONLY the information provided", StringComparison.OrdinalIgnoreCase))
                {
                    return new ChatCompletionResponse("Sub-answer", null);
                }

                return new ChatCompletionResponse("Final answer", null);
            });

        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("drop", "Drop after normalization", 0.95),
                new VectorSearchResult("keep", "Keep after normalization", 0.94)
            ]);

        var reranker = new Mock<IRerankerClient>();
        reranker.Setup(r => r.RerankAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<VectorSearchResult>>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync([
                new VectorSearchResult("drop", "Drop after normalization", 12.0),
                new VectorSearchResult("keep", "Keep after normalization", 20.0)
            ]);

        var service = BuildService(
            chat.Object,
            vectorStore: vectorStore.Object,
            reranker: reranker.Object,
            enableReranker: true,
            queryRewriterModel: "query-decomp-model");
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [new Message("user", "Compare revenue")],
            CollectionNames: ["docs"],
            EnableQueryDecomposition: true,
            EnableReranker: true,
            ConfidenceThreshold: 0.80);

        await service.GenerateAsync(context.Request, prompt);

        var finalPrompt = captured.Last().Messages[1].Content.ToString();
        finalPrompt.Should().Contain("Keep after normalization");
        finalPrompt.Should().NotContain("Drop after normalization");
    }

    [Fact]
    public async Task GenerateAsync_WithSingleQueryDecomposition_UsesDirectRagPath()
    {
        var captured = new List<ChatCompletionRequest>();
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((request, _) => captured.Add(request))
            .ReturnsAsync((ChatCompletionRequest request, CancellationToken _) =>
            {
                var system = request.Messages[0].Content.ToString() ?? string.Empty;
                if (system.Contains("break down a user's complex question", StringComparison.OrdinalIgnoreCase))
                {
                    return new ChatCompletionResponse("Revenue question", null);
                }

                if (system.Contains("Answer using ONLY the information provided", StringComparison.OrdinalIgnoreCase)
                    || system.Contains("identifying missing information", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Iterative decomposition prompts should not run for one subquery.");
                }

                return new ChatCompletionResponse("Final answer", null);
            });

        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(s => s.SearchAsync(
                "docs", "Compare revenue", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("chunk-1", "Direct context", 0.95)
            ]);

        var service = BuildService(
            chat.Object,
            vectorStore: vectorStore.Object,
            queryRewriterModel: "query-decomp-model");
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [new Message("user", "Compare revenue")],
            CollectionNames: ["docs"],
            EnableQueryDecomposition: true,
            EnableReranker: false,
            ConfidenceThreshold: 0.0);

        await service.GenerateAsync(context.Request, prompt);

        var finalRequest = captured.Last();
        finalRequest.Messages[1].Content.ToString().Should().Contain("Direct context");
        finalRequest.Messages[1].Content.ToString().Should().NotContain("Sub-answer");
        captured.Should().HaveCount(2);
        vectorStore.Verify(s => s.SearchAsync(
            "docs", "Compare revenue", 100, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_WithAgenticRequest_ReturnsUnavailableAndDoesNotCallLlm()
    {
        var chat = new Mock<IChatCompletionService>();
        var service = BuildService(chat.Object);
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt([new Message("user", "Hello")], Agentic: true);

        var result = await service.GenerateAsync(context.Request, prompt);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        chat.Verify(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_WithAgenticRequest_DelegatesThroughAgenticService()
    {
        var chat = new Mock<IChatCompletionService>();
        var agentic = new Mock<IAgenticRagService>();
        agentic.Setup(a => a.IsRequested(It.IsAny<Prompt>())).Returns(true);
        agentic.Setup(a => a.GenerateAsync(It.IsAny<AgenticRagInvocation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Results.Accepted());
        var service = BuildService(chat.Object, agenticRagService: agentic.Object);
        var context = BuildContext("/generate");
        var prompt = new Prompt(
            [new Message("user", "Hello")],
            CollectionNames: ["docs"],
            Agentic: true);

        var result = await service.GenerateAsync(context.Request, prompt);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status202Accepted);
        agentic.Verify(a => a.GenerateAsync(
            It.Is<AgenticRagInvocation>(invocation =>
                invocation.UserQuery == "Hello"
                && invocation.RequestPath == "/generate"
                && invocation.CollectionNames.SequenceEqual(new[] { "docs" })),
            It.IsAny<CancellationToken>()), Times.Once);
        chat.Verify(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_WithAgenticEnabledByConfig_ReturnsUnavailableAndDoesNotCallLlm()
    {
        var chat = new Mock<IChatCompletionService>();
        var service = BuildService(chat.Object, enableAgenticRag: true);
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt([new Message("user", "Hello")], Agentic: null);

        var result = await service.GenerateAsync(context.Request, prompt);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        chat.Verify(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_WithRerankerTopKGreaterThanVdbTopK_ReturnsBadRequest()
    {
        var chat = new Mock<IChatCompletionService>();
        var service = BuildService(chat.Object);
        var context = BuildContext("/chat/completions");
        var prompt = new Prompt(
            [new Message("user", "Hello")],
            VdbTopK: 5,
            RerankerTopK: 6);

        var result = await service.GenerateAsync(context.Request, prompt);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        chat.Verify(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchAsync_WithInvalidConfidenceThreshold_ReturnsBadRequest()
    {
        var chat = new Mock<IChatCompletionService>();
        var service = BuildService(chat.Object);
        var context = BuildContext("/search");
        var search = new DocumentSearch(
            Query: "Hello",
            ConfidenceThreshold: 1.1);

        var result = await service.SearchAsync(context.Request, search);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task VectorStoreSearchAsync_WithInvalidRanker_ReturnsBadRequest()
    {
        var chat = new Mock<IChatCompletionService>();
        var service = BuildService(chat.Object);
        var context = BuildContext("/v2/vector_stores/docs/search");

        var result = await service.VectorStoreSearchAsync(
            context.Request,
            "docs",
            new VectorStoreSearchRequest(
                Query: "Hello",
                RankingOptions: new RankingOptions(Ranker: "unknown")));

        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    private static RagService BuildService(
        IChatCompletionService chat,
        IVectorStore? vectorStore = null,
        IRerankerClient? reranker = null,
        IChatCompletionClientFactory? chatCompletionClientFactory = null,
        IVectorStoreClientFactory? vectorStoreClientFactory = null,
        bool enableReranker = false,
        string? queryRewriterModel = null,
        int conversationHistory = 0,
        IServiceProvider? serviceProvider = null,
        bool filterThinkTokens = true,
        bool enableAgenticRag = false,
        IAgenticRagService? agenticRagService = null,
        ICitationAssetResolver? citationAssetResolver = null,
        IVlmContextAssembler? vlmContextAssembler = null)
    {
        var config = new RagServerConfiguration
        {
            CollectionName = "docs",
            EnableReranker = enableReranker,
            EnableReflection = false,
            EnableFilterGenerator = false,
            QueryRewriterModel = queryRewriterModel ?? "",
            ConversationHistory = conversationHistory,
            FilterThinkTokens = filterThinkTokens,
            EnableAgenticRag = enableAgenticRag
        };
        var prompts = PromptCatalog.Load(null);
        var store = vectorStore ?? Mock.Of<IVectorStore>();
        var management = Mock.Of<IVectorStoreManagement>(m =>
            m.CheckHealthAsync(It.IsAny<CancellationToken>()) == Task.FromResult(true));
        var activeReranker = reranker ?? Mock.Of<IRerankerClient>();
        var summary = Mock.Of<ISummarizationService>();
        var queryRewriter = new QueryRewritingService(
            chat,
            config,
            prompts,
            NullLogger<QueryRewritingService>.Instance);
        var queryDecomposition = new QueryDecompositionService(
            chat,
            store,
            activeReranker,
            config,
            prompts,
            NullLogger<QueryDecompositionService>.Instance);
        var reflection = new ReflectionService(
            chat,
            config,
            prompts,
            NullLogger<ReflectionService>.Instance);
        var filter = new FilterExpressionService(
            chat,
            Mock.Of<IVectorStoreFilterCapabilities>(),
            config,
            prompts,
            NullLogger<FilterExpressionService>.Instance);
        var defaultCitationAssetResolver = new Mock<ICitationAssetResolver>();
        defaultCitationAssetResolver.Setup(r => r.ResolveAsync(
                It.IsAny<VectorSearchResult>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CitationAsset?)null);

        return new RagService(
            config,
            NullLogger<RagService>.Instance,
            chat,
            chatCompletionClientFactory ?? Mock.Of<IChatCompletionClientFactory>(),
            store,
            management,
            vectorStoreClientFactory ?? Mock.Of<IVectorStoreClientFactory>(),
            activeReranker,
            summary,
            queryRewriter,
            queryDecomposition,
            reflection,
            filter,
            agenticRagService ?? new UnavailableAgenticRagService(
                config,
                NullLogger<UnavailableAgenticRagService>.Instance),
            citationAssetResolver ?? defaultCitationAssetResolver.Object,
            vlmContextAssembler ?? new VlmContextAssembler(),
            new RagMetrics(),
            prompts,
            serviceProvider ?? new ServiceCollection().BuildServiceProvider());
    }

    private static Mock<IChatCompletionService> MockChat(Action<ChatCompletionRequest> capture)
    {
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((request, _) => capture(request))
            .ReturnsAsync(new ChatCompletionResponse("answer", null));
        chat.Setup(c => c.StreamAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Returns((ChatCompletionRequest request, CancellationToken cancellationToken) =>
            {
                capture(request);
                return ToAsyncEnumerable(["answer"], cancellationToken);
            });
        chat.Setup(c => c.StreamDeltasAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Returns((ChatCompletionRequest request, CancellationToken cancellationToken) =>
            {
                capture(request);
                return ToAsyncDeltaEnumerable([new ChatStreamDelta(Content: "answer")], cancellationToken);
            });
        return chat;
    }

    private static DefaultHttpContext BuildContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async IAsyncEnumerable<string> ToAsyncEnumerable(
        IEnumerable<string> values,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ChatStreamDelta> ToAsyncDeltaEnumerable(
        IEnumerable<ChatStreamDelta> values,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            await Task.Yield();
        }
    }
}
