using System.Net;
using System.Text.Json;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Options;
using DotnetRag.Shared.VectorStore;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace DotnetRag.Tests.Unit.Services;

public sealed class MilvusVectorStoreTests
{
    [Fact]
    public async Task CheckHealthAsync_UsesPostCollectionList_AndMilvusEnvelope()
    {
        var handler = new RecordingHandler("""{"code":0,"data":["test"]}""");
        var store = CreateStore(handler);

        var healthy = await store.CheckHealthAsync();

        healthy.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.AbsoluteUri.Should()
            .Be("http://milvus:19530/v2/vectordb/collections/list");
    }

    [Fact]
    public async Task CollectionExistsAsync_TreatsNonzeroMilvusCodeAsMissing()
    {
        var handler = new RecordingHandler(
            """{"code":100,"message":"can't find collection[database=default][collection=missing]"}""");
        var store = CreateStore(handler);

        var exists = await store.CollectionExistsAsync("missing");

        exists.Should().BeFalse();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.AbsoluteUri.Should()
            .Be("http://milvus:19530/v2/vectordb/collections/describe");
        handler.RequestBodies[0].Should().Contain("\"collectionName\":\"missing\"");
    }

    [Fact]
    public async Task UpsertAsync_CreatesCollectionWithConfiguredDimension_AndPassesBearerToken()
    {
        var handler = new RecordingHandler(
            """{"code":100,"message":"missing"}""",
            """{"code":0,"data":{}}""",
            FullSchemaResponse,
            """{"code":0,"data":{"upsertCount":1}}""");
        var store = CreateStore(handler, token: "secret");

        await store.UpsertAsync(
            "docs",
            [
                new VectorDocument(
                    "doc-1",
                    "hello",
                    new Dictionary<string, object?> { ["filename"] = "doc.txt" },
                    [0.1f, 0.2f, 0.3f])
            ]);

        handler.Requests.Select(item => item.RequestUri!.AbsolutePath).Should().Equal(
            "/v2/vectordb/collections/describe",
            "/v2/vectordb/collections/create",
            "/v2/vectordb/collections/describe",
            "/v2/vectordb/entities/upsert");
        foreach (var request in handler.Requests)
        {
            request.Headers.Authorization.Should().NotBeNull();
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be("secret");
        }
        handler.RequestBodies[1].Should().Contain("\"dim\":\"384\"");
        handler.RequestBodies[1].Should().Contain("\"fieldName\":\"pk\"");
        handler.RequestBodies[1].Should().Contain("\"autoId\":true");
        handler.RequestBodies[1].Should().Contain("\"fieldName\":\"vector\"");
        handler.RequestBodies[1].Should().Contain("\"fieldName\":\"source\"");
        handler.RequestBodies[1].Should().Contain("\"fieldName\":\"content_metadata\"");
        handler.RequestBodies[1].Should().Contain("\"indexName\":\"idx_vector\"");
        handler.RequestBodies[3].Should().Contain("\"filename\":\"doc.txt\"");
        handler.RequestBodies[3].Should().Contain("\"pk\":");
        handler.RequestBodies[3].Should().Contain("\"vector\":[0.1,0.2,0.3]");
        handler.RequestBodies[3].Should().Contain("\"source_id\":\"doc.txt\"");
        handler.RequestBodies[3].Should().Contain("\"content_metadata\"");
    }

    [Fact]
    public async Task UpsertAsync_OmitsDotnetCompatibilityFields_WhenCollectionUsesPythonSchema()
    {
        var handler = new RecordingHandler(
            """{"code":0,"data":{"collectionName":"docs"}}""",
            PythonSchemaResponse,
            """{"code":0,"data":{"upsertCount":1}}""");
        var store = CreateStore(handler);

        await store.UpsertAsync(
            "docs",
            [
                new VectorDocument(
                    "doc-1",
                    "hello",
                    new Dictionary<string, object?> { ["filename"] = "doc.txt" },
                    [0.1f, 0.2f, 0.3f])
            ]);

        handler.RequestBodies[2].Should().Contain("\"pk\":");
        handler.RequestBodies[2].Should().Contain("\"text\":\"hello\"");
        handler.RequestBodies[2].Should().Contain("\"vector\":[0.1,0.2,0.3]");
        handler.RequestBodies[2].Should().Contain("\"source_id\":\"doc.txt\"");
        handler.RequestBodies[2].Should().Contain("\"content_metadata\"");
        handler.RequestBodies[2].Should().NotContain("\"id\":\"doc-1\"");
        handler.RequestBodies[2].Should().NotContain("\"metadata\":");
    }


    [Fact]
    public async Task UpsertAsync_Throws_WhenMilvusReturnsHttpOkWithNonzeroCode()
    {
        var handler = new RecordingHandler(
            """{"code":0,"data":{"collectionName":"docs"}}""",
            FullSchemaResponse,
            """{"code":1804,"message":"vector dimension mismatch"}""");
        var store = CreateStore(handler);

        var act = async () => await store.UpsertAsync(
            "docs",
            [new VectorDocument("doc-1", "hello", Embedding: [0.1f])]);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*vector dimension mismatch*");
    }

    [Fact]
    public async Task DeleteDocumentsAsync_Throws_WhenMilvusDeleteEnvelopeFails()
    {
        var handler = new RecordingHandler(
            FullSchemaResponse,
            """{"code":999,"message":"delete failed"}""");
        var store = CreateStore(handler);

        var act = async () => await store.DeleteDocumentsAsync("docs", ["doc.txt"]);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*delete failed*");
    }

    [Fact]
    public async Task GetDocumentTextByIdAsync_QueriesMilvusById()
    {
        var handler = new RecordingHandler(
            FullSchemaResponse,
            FullSchemaResponse,
            """{"code":0,"data":[{"text":"summary text"}]}""");
        var store = CreateStore(handler);

        var text = await store.GetDocumentTextByIdAsync("summary_docs", "summary_file.txt");

        text.Should().Be("summary text");
        handler.Requests.Select(item => item.RequestUri!.AbsolutePath).Should().Equal(
            "/v2/vectordb/collections/describe",
            "/v2/vectordb/collections/describe",
            "/v2/vectordb/entities/query");
        using var body = JsonDocument.Parse(handler.RequestBodies[2]);
        var root = body.RootElement;
        root.GetProperty("collectionName").GetString().Should().Be("summary_docs");
        root.GetProperty("filter").GetString().Should().Be("id == \"summary_file.txt\"");
        root.GetProperty("outputFields")[0].GetString().Should().Be("text");
    }

    private static MilvusVectorStore CreateStore(
        RecordingHandler handler,
        string? token = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient("milvus"))
            .Returns(new HttpClient(handler));

        var logger = new Mock<ILogger<MilvusVectorStore>>();

        return new MilvusVectorStore(
            factory.Object,
            new StaticEmbeddingService(),
            new VectorStoreOptions
            {
                Provider = "milvus",
                Endpoint = "http://milvus:19530"
            },
            embeddingDim: 384,
            token,
            logger.Object);
    }

    private const string FullSchemaResponse =
        """
        {"code":0,"data":{"fields":[
          {"fieldName":"pk"},
          {"fieldName":"id"},
          {"fieldName":"text"},
          {"fieldName":"vector"},
          {"fieldName":"source"},
          {"fieldName":"content_metadata"},
          {"fieldName":"metadata"}
        ]}}
        """;

    private const string PythonSchemaResponse =
        """
        {"code":0,"data":{"fields":[
          {"fieldName":"pk"},
          {"fieldName":"text"},
          {"fieldName":"vector"},
          {"fieldName":"source"},
          {"fieldName":"content_metadata"}
        ]}}
        """;

    private sealed class StaticEmbeddingService : IEmbeddingService
    {
        public Task<IReadOnlyList<float>> EmbedAsync(
            string text,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float>>([0.1f, 0.2f, 0.3f]);
    }

    private sealed class RecordingHandler(params string[] responseBodies) : HttpMessageHandler
    {
        private readonly Queue<string> _responseBodies = new(responseBodies);

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            Requests.Add(CloneRequest(request));

            var responseBody = _responseBodies.Count > 0
                ? _responseBodies.Dequeue()
                : """{"code":0,"data":{}}""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }
}
