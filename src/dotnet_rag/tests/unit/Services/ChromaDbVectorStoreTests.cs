using System.Net;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Options;
using DotnetRag.Shared.VectorStore;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace DotnetRag.Tests.Unit.Services;

public sealed class ChromaDbVectorStoreTests
{
    [Fact]
    public async Task SearchAsync_TranslatesSimpleContentMetadataFilter_ToChromaWhere()
    {
        var handler = new RecordingHandler(
            """{"id":"collection-id","name":"docs"}""",
            """{"ids":[["chunk-1"]],"documents":[["hello"]],"distances":[[0.1]],"metadatas":[[{"filename":"doc.txt"}]]}""");
        var store = CreateStore(handler);

        var results = await store.SearchAsync(
            "docs",
            "query",
            5,
            "content_metadata[\"year\"] == \"2024\"");

        results.Should().ContainSingle();
        handler.RequestBodies[1].Should().Contain("\"where\"");
        handler.RequestBodies[1].Should().Contain("\"year\":2024");
    }

    [Fact]
    public async Task SearchAsync_TranslatesParenthesizedAndFilter_ToChromaWhere()
    {
        var handler = new RecordingHandler(
            """{"id":"collection-id","name":"docs"}""",
            """{"ids":[["chunk-1"]],"documents":[["hello"]],"distances":[[0.1]],"metadatas":[[{"filename":"doc.txt"}]]}""");
        var store = CreateStore(handler);

        await store.SearchAsync(
            "docs",
            "query",
            5,
            """(content_metadata["year"] >= 2024 and content_metadata["is_public"] == true)""");

        handler.RequestBodies[1].Should().Contain("\"where\"");
        handler.RequestBodies[1].Should().Contain("\"$and\"");
        handler.RequestBodies[1].Should().Contain("\"year\":{\"$gte\":2024}");
        handler.RequestBodies[1].Should().Contain("\"is_public\":true");
    }

    [Fact]
    public async Task SearchAsync_TranslatesInFilter_ToChromaWhere()
    {
        var handler = new RecordingHandler(
            """{"id":"collection-id","name":"docs"}""",
            """{"ids":[["chunk-1"]],"documents":[["hello"]],"distances":[[0.1]],"metadatas":[[{"filename":"doc.txt"}]]}""");
        var store = CreateStore(handler);

        await store.SearchAsync(
            "docs",
            "query",
            5,
            """content_metadata["year"] in [2024, 2025]""");

        handler.RequestBodies[1].Should().Contain("\"where\"");
        handler.RequestBodies[1].Should().Contain("\"year\":{\"$in\":[2024,2025]}");
    }

    [Fact]
    public async Task SearchAsync_TranslatesOrFilter_ToChromaWhere()
    {
        var handler = new RecordingHandler(
            """{"id":"collection-id","name":"docs"}""",
            """{"ids":[["chunk-1"]],"documents":[["hello"]],"distances":[[0.1]],"metadatas":[[{"filename":"doc.txt"}]]}""");
        var store = CreateStore(handler);

        await store.SearchAsync(
            "docs",
            "query",
            5,
            """content_metadata["region"] == "EMEA" OR content_metadata["region"] == "APAC" """);

        handler.RequestBodies[1].Should().Contain("\"where\"");
        handler.RequestBodies[1].Should().Contain("\"$or\"");
        handler.RequestBodies[1].Should().Contain("\"region\":\"EMEA\"");
        handler.RequestBodies[1].Should().Contain("\"region\":\"APAC\"");
    }

    [Fact]
    public async Task SearchAsync_TranslatesNotInFilter_ToChromaWhere()
    {
        var handler = new RecordingHandler(
            """{"id":"collection-id","name":"docs"}""",
            """{"ids":[["chunk-1"]],"documents":[["hello"]],"distances":[[0.1]],"metadatas":[[{"filename":"doc.txt"}]]}""");
        var store = CreateStore(handler);

        await store.SearchAsync(
            "docs",
            "query",
            5,
            """metadata["status"] not in ["draft", "archived"]""");

        handler.RequestBodies[1].Should().Contain("\"where\"");
        handler.RequestBodies[1].Should().Contain("\"status\":{\"$nin\":[\"draft\",\"archived\"]}");
    }

    [Fact]
    public async Task SearchAsync_TranslatesNestedLogicalFilter_ToChromaWhere()
    {
        var handler = new RecordingHandler(
            """{"id":"collection-id","name":"docs"}""",
            """{"ids":[["chunk-1"]],"documents":[["hello"]],"distances":[[0.1]],"metadatas":[[{"filename":"doc.txt"}]]}""");
        var store = CreateStore(handler);

        await store.SearchAsync(
            "docs",
            "query",
            5,
            """(content_metadata["year"] >= 2024 and (content_metadata["region"] == "EMEA" or content_metadata["region"] == "APAC"))""");

        handler.RequestBodies[1].Should().Contain("\"where\"");
        handler.RequestBodies[1].Should().Contain("\"$and\"");
        handler.RequestBodies[1].Should().Contain("\"$or\"");
        handler.RequestBodies[1].Should().Contain("\"year\":{\"$gte\":2024}");
        handler.RequestBodies[1].Should().Contain("\"region\":\"EMEA\"");
        handler.RequestBodies[1].Should().Contain("\"region\":\"APAC\"");
    }

    [Fact]
    public async Task SearchAsync_TranslatesSingleQuotedContentMetadataFilter_ToChromaWhere()
    {
        var handler = new RecordingHandler(
            """{"id":"collection-id","name":"docs"}""",
            """{"ids":[["chunk-1"]],"documents":[["hello"]],"distances":[[0.1]],"metadatas":[[{"filename":"doc.txt"}]]}""");
        var store = CreateStore(handler);

        await store.SearchAsync(
            "docs",
            "query",
            5,
            "content_metadata['department'] == 'finance'");

        handler.RequestBodies[1].Should().Contain("\"where\"");
        handler.RequestBodies[1].Should().Contain("\"department\":\"finance\"");
    }

    [Fact]
    public async Task SearchAsync_TranslatesPythonSourceFilter_ToFlattenedChromaWhere()
    {
        var handler = new RecordingHandler(
            """{"id":"collection-id","name":"docs"}""",
            """{"ids":[["chunk-1"]],"documents":[["hello"]],"distances":[[0.1]],"metadatas":[[{"filename":"doc.txt"}]]}""");
        var store = CreateStore(handler);

        await store.SearchAsync(
            "docs",
            "query",
            5,
            """source["source_id"] == "report.pdf" """);

        handler.RequestBodies[1].Should().Contain("\"where\"");
        handler.RequestBodies[1].Should().Contain("\"source.source_id\":\"report.pdf\"");
    }

    [Fact]
    public async Task SearchAsync_WithNumericAndBooleanMetadata_ReturnsStringMetadataValues()
    {
        var handler = new RecordingHandler(
            """{"id":"collection-id","name":"docs"}""",
            """{"ids":[["chunk-1"]],"documents":[["hello"]],"distances":[[0.1]],"metadatas":[[{"year":2024,"is_public":true,"filename":"doc.txt"}]]}""");
        var store = CreateStore(handler);

        var results = await store.SearchAsync("docs", "query", 5);

        results.Should().ContainSingle();
        results[0].Metadata!["year"].Should().Be("2024");
        results[0].Metadata!["is_public"].Should().Be("true");
        results[0].Metadata!["filename"].Should().Be("doc.txt");
    }

    [Fact]
    public void ChromaDbVectorStore_DoesNotSupportGeneratedFilters()
    {
        var store = CreateStore(new RecordingHandler());

        store.SupportsGeneratedFilters.Should().BeFalse();
        store.GeneratedFilterPromptKind.Should().Be(GeneratedFilterPromptKind.None);
    }

    [Fact]
    public async Task ManagementListCollectionsAsync_ReturnsDetailedCollectionInfoThroughInterface()
    {
        var handler = new RecordingHandler(
            """
            [
              {"id":"docs-id","name":"docs"},
              {"id":"metadata-id","name":"metadata_schema"},
              {"id":"info-id","name":"document_info"},
              {"id":"archive-id","name":"archive"}
            ]
            """,
            """{"id":"metadata-id","name":"metadata_schema"}""",
            """
            {
              "ids":["schema-1","schema-2"],
              "metadatas":[
                {"collection_name":"docs","metadata_schema":"[{\"name\":\"year\",\"type\":\"int\"}]"},
                {"collection_name":"archive","metadata_schema":"[{\"name\":\"region\",\"type\":\"str\"}]"}
              ],
              "documents":[null,null]
            }
            """,
            """{"id":"info-id","name":"document_info"}""",
            """
            {
              "ids":["info-1","info-2"],
              "metadatas":[
                {"collection_name":"docs","info_type":"collection"},
                {"collection_name":"docs","info_type":"catalog"}
              ],
              "documents":[
                "{\"document_count\":2}",
                "{\"summary\":\"annual reports\"}"
              ]
            }
            """,
            "2",
            "1");
        IVectorStoreManagement store = CreateStore(handler);

        var collections = await store.ListCollectionsAsync();

        collections.Select(item => item.CollectionName).Should().Equal("docs", "archive");
        collections[0].NumEntities.Should().Be(2);
        collections[0].MetadataSchema.Should().ContainSingle()
            .Which["name"].Should().Be("year");
        collections[0].CollectionInfo["document_count"].Should().Be(2L);
        collections[0].CollectionInfo["summary"].Should().Be("annual reports");
        collections[1].NumEntities.Should().Be(1);
        collections[1].MetadataSchema.Should().ContainSingle()
            .Which["name"].Should().Be("region");
        handler.Requests.Select(item => item.RequestUri!.AbsolutePath).Should().Equal(
            "/api/v2/tenants/default_tenant/databases/default_database/collections",
            "/api/v2/tenants/default_tenant/databases/default_database/collections/metadata_schema",
            "/api/v2/tenants/default_tenant/databases/default_database/collections/metadata-id/get",
            "/api/v2/tenants/default_tenant/databases/default_database/collections/document_info",
            "/api/v2/tenants/default_tenant/databases/default_database/collections/info-id/get",
            "/api/v2/tenants/default_tenant/databases/default_database/collections/docs-id/count",
            "/api/v2/tenants/default_tenant/databases/default_database/collections/archive-id/count");
    }

    private static ChromaDbVectorStore CreateStore(RecordingHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient("chroma"))
            .Returns(new HttpClient(handler));

        return new ChromaDbVectorStore(
            factory.Object,
            new StaticEmbeddingService(),
            new VectorStoreOptions
            {
                Endpoint = "http://chroma:8000"
            },
            Mock.Of<ILogger<ChromaDbVectorStore>>());
    }

    private sealed class StaticEmbeddingService : IEmbeddingService
    {
        public Task<IReadOnlyList<float>> EmbedAsync(
            string text,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float>>([0.1f, 0.2f, 0.3f]);
    }

    private sealed class RecordingHandler(params string[] responseBodies) : HttpMessageHandler
    {
        private readonly Queue<string> responseBodies = new(responseBodies);

        public List<string> RequestBodies { get; } = [];
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);

            var responseBody = responseBodies.Count > 0
                ? responseBodies.Dequeue()
                : "{}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        }
    }
}
