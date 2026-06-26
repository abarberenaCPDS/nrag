using DotnetRag.Ingestor.Models;
using DotnetRag.Ingestor.Services;
using DotnetRag.Ingestor.Services.ObjectStore;
using FluentAssertions;
using Moq;
using System.Net;

namespace DotnetRag.Tests.Unit.Services;

public sealed class PersistenceAndPipelineTests
{
    [Fact]
    public void InMemoryIngestorStore_ReloadsCatalog_WhenCatalogPathConfigured()
    {
        var previous = Environment.GetEnvironmentVariable("APP_INGESTOR_CATALOG_PATH");
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-catalog.json");
        try
        {
            Environment.SetEnvironmentVariable("APP_INGESTOR_CATALOG_PATH", path);
            var store = new InMemoryIngestorStore();
            store.CreateCollection(new CreateCollectionRequest
            {
                CollectionName = "persisted",
                Description = "catalog"
            });
            store.UpsertDocuments(
                "persisted",
                [new InMemoryIngestorStore.StoredDocument
                {
                    DocumentName = "doc.txt",
                    Metadata = new Dictionary<string, object?> { ["filename"] = "doc.txt" },
                    DocumentInfo = new Dictionary<string, object?> { ["document_type"] = "txt" }
                }],
                replaceExisting: false);

            var reloaded = new InMemoryIngestorStore();

            reloaded.CollectionExists("persisted").Should().BeTrue();
            reloaded.GetDocumentNames("persisted").Should().ContainSingle("doc.txt");
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_INGESTOR_CATALOG_PATH", previous);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void InMemoryIngestorStore_UsesInjectedCatalogStore()
    {
        var catalog = new RecordingCatalogStore();
        catalog.LoadedEntries.Add(new IngestorCatalogEntry
        {
            Name = "backend_catalog",
            Description = "from backend"
        });
        var store = new InMemoryIngestorStore(catalog);

        store.CollectionExists("backend_catalog").Should().BeTrue();
        store.CreateCollection(new CreateCollectionRequest { CollectionName = "created" });

        catalog.SavedEntries.Should().Contain(entry => entry.Name == "backend_catalog");
        catalog.SavedEntries.Should().Contain(entry => entry.Name == "created");
    }

    [Fact]
    public void FileBackedIngestionTaskStore_ReloadsTaskState()
    {
        var previous = Environment.GetEnvironmentVariable("APP_INGESTION_TASK_STORE_PATH");
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-tasks.json");
        try
        {
            Environment.SetEnvironmentVariable("APP_INGESTION_TASK_STORE_PATH", path);
            var store = new FileBackedIngestionTaskStore();
            store.Set("task-1", new IngestionTaskStatusResponse
            {
                State = "FINISHED",
                Result = new UploadDocumentResponse { Message = "done" }
            });

            var reloaded = new FileBackedIngestionTaskStore();

            reloaded.TryGet("task-1", out var status).Should().BeTrue();
            status.State.Should().Be("FINISHED");
            status.Result.Message.Should().Be("done");
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_INGESTION_TASK_STORE_PATH", previous);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task FileBackedIngestionJobQueue_ClaimsCompletesAndPersistsJobFiles()
    {
        var previous = Environment.GetEnvironmentVariable("APP_INGESTION_JOB_QUEUE_PATH");
        var root = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-jobs");
        try
        {
            Environment.SetEnvironmentVariable("APP_INGESTION_JOB_QUEUE_PATH", root);
            var queue = new FileBackedIngestionJobQueue();
            var job = new IngestionJob
            {
                TaskId = "task-1",
                Payload = new DocumentUploadRequest
                {
                    CollectionName = "queued"
                },
                FilePaths = ["/tmp/doc.txt"],
                BearerToken = "secret"
            };

            await queue.EnqueueAsync(job);
            File.Exists(Path.Combine(root, "pending", "task-1.json")).Should().BeTrue();

            var claimed = await queue.TryClaimAsync();

            claimed.Should().NotBeNull();
            claimed!.TaskId.Should().Be("task-1");
            claimed.BearerToken.Should().Be("secret");
            File.Exists(Path.Combine(root, "processing", "task-1.json")).Should().BeTrue();

            await queue.CompleteAsync(claimed);

            File.Exists(Path.Combine(root, "processing", "task-1.json")).Should().BeFalse();
            File.Exists(Path.Combine(root, "completed", "task-1.json")).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_INGESTION_JOB_QUEUE_PATH", previous);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LocalIngestionPipeline_ExtractsHtmlText()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.html");
        try
        {
            await File.WriteAllTextAsync(path, "<h1>Hello</h1><p>World</p>");
            var pipeline = new LocalIngestionPipeline();

            var text = await pipeline.ExtractTextAsync(path, Path.GetFileName(path));

            text.Should().Contain("Hello");
            text.Should().Contain("World");
            pipeline.SupportsMultimodalExtraction.Should().BeFalse();
            pipeline.SupportsObjectStoreAssets.Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void IngestionPipelines_ExposeBackendSpecificFileSupport()
    {
        IIngestionPipeline local = new LocalIngestionPipeline();
        IIngestionPipeline external = new ExternalIngestionPipeline("nrl");

        local.SupportsFile("document.pdf").Should().BeTrue();
        local.SupportsFile("audio.mp3").Should().BeFalse();
        local.SupportsFile("unknown.bin").Should().BeFalse();

        external.SupportsFile("audio.mp3").Should().BeTrue();
        external.SupportsFile("unknown.bin").Should().BeFalse();
    }

    [Fact]
    public async Task FileObjectStoreService_StoresListsAndDeletesJsonPayloads()
    {
        var previous = Environment.GetEnvironmentVariable("APP_OBJECT_STORE_ROOT");
        var root = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-object-store");
        try
        {
            Environment.SetEnvironmentVariable("APP_OBJECT_STORE_ROOT", root);
            var store = new FileObjectStoreService();

            await store.StoreJsonAsync(
                "collection",
                "doc.pdf/citation.json",
                new Dictionary<string, object?> { ["filename"] = "doc.pdf" });

            var objects = await store.ListAsync("collection", "doc.pdf/");
            objects.Should().ContainSingle("doc.pdf/citation.json");

            await store.DeleteAsync("collection", objects);
            (await store.ListAsync("collection", "doc.pdf/")).Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_OBJECT_STORE_ROOT", previous);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task IngestionPipelineFactory_SelectsExternalBackend_WhenConfigured()
    {
        var previousBackend = Environment.GetEnvironmentVariable("APP_INGESTION_BACKEND");
        var previousEndpoint = Environment.GetEnvironmentVariable("APP_NRL_ENDPOINT");
        try
        {
            Environment.SetEnvironmentVariable("APP_INGESTION_BACKEND", "nrl");
            Environment.SetEnvironmentVariable("APP_NRL_ENDPOINT", null);
            var factory = new Mock<IHttpClientFactory>();

            var pipeline = IngestionPipelineFactory.Create(factory.Object);

            pipeline.BackendName.Should().Be("nrl");
            pipeline.SupportsMultimodalExtraction.Should().BeTrue();
            var act = async () => await pipeline.ExtractTextAsync("missing.pdf", "missing.pdf");
            await act.Should().ThrowAsync<NotSupportedException>();
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_INGESTION_BACKEND", previousBackend);
            Environment.SetEnvironmentVariable("APP_NRL_ENDPOINT", previousEndpoint);
        }
    }

    [Fact]
    public async Task HttpExternalIngestionPipeline_ParsesFlexibleBridgeResponse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(path, "input");
            using var http = new HttpClient(new StubHandler("""
                {
                  "text": "extracted text",
                  "document_info": {
                    "total_elements": 9,
                    "has_images": true
                  },
                  "asset_object_names": [
                    "doc/image-1.png"
                  ]
                }
                """))
            {
                BaseAddress = new Uri("http://localhost")
            };
            var pipeline = new HttpExternalIngestionPipeline(
                http,
                "nvingest",
                new Uri("http://localhost/extract"),
                "token");

            var result = await pipeline.ExtractAsync(path, Path.GetFileName(path));

            result.Text.Should().Be("extracted text");
            result.DocumentInfo["total_elements"].Should().Be(9L);
            result.DocumentInfo["has_images"].Should().Be(true);
            result.AssetObjectNames.Should().ContainSingle("doc/image-1.png");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void IngestionPipelineFactory_AppendsBridgePath_ForBaseEndpoint()
    {
        var previousBackend = Environment.GetEnvironmentVariable("APP_INGESTION_BACKEND");
        var previousEndpoint = Environment.GetEnvironmentVariable("APP_NVINGEST_ENDPOINT");
        try
        {
            Environment.SetEnvironmentVariable("APP_INGESTION_BACKEND", "nvingest");
            Environment.SetEnvironmentVariable("APP_NVINGEST_ENDPOINT", "http://localhost:8082");
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(item => item.CreateClient(It.IsAny<string>()))
                .Returns(new HttpClient(new StubHandler("{}")));

            var pipeline = IngestionPipelineFactory.Create(factory.Object);

            pipeline.Should().BeOfType<HttpExternalIngestionPipeline>();
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_INGESTION_BACKEND", previousBackend);
            Environment.SetEnvironmentVariable("APP_NVINGEST_ENDPOINT", previousEndpoint);
        }
    }

    [Fact]
    public void InMemoryIngestorStore_ExposesCollectionMetrics()
    {
        var store = new InMemoryIngestorStore();
        store.CreateCollection(new CreateCollectionRequest { CollectionName = "metrics" });
        store.UpsertDocuments(
            "metrics",
            [
                new InMemoryIngestorStore.StoredDocument
                {
                    DocumentName = "a.pdf",
                    Metadata = [],
                    DocumentInfo = new Dictionary<string, object?>
                    {
                        ["document_type"] = "pdf",
                        ["total_elements"] = 3,
                        ["raw_text_elements_size"] = 120,
                        ["has_images"] = false,
                        ["last_indexed"] = "2026-01-01T00:00:00Z",
                        ["ingestion_status"] = "completed"
                    }
                },
                new InMemoryIngestorStore.StoredDocument
                {
                    DocumentName = "b.txt",
                    Metadata = [],
                    DocumentInfo = new Dictionary<string, object?>
                    {
                        ["document_type"] = "txt",
                        ["total_elements"] = 2,
                        ["raw_text_elements_size"] = 80,
                        ["has_images"] = true,
                        ["last_indexed"] = "2026-01-02T00:00:00Z",
                        ["ingestion_status"] = "completed"
                    }
                }
            ],
            replaceExisting: false);

        var collection = store.GetCollections().Single(item => item.CollectionName == "metrics");

        collection.CollectionInfo["number_of_files"].Should().Be(2);
        collection.CollectionInfo["total_elements"].Should().Be(5L);
        collection.CollectionInfo["raw_text_elements_size"].Should().Be(200L);
        collection.CollectionInfo["has_images"].Should().Be(true);
        collection.CollectionInfo["last_indexed"].Should().Be("2026-01-02T00:00:00Z");
    }

    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Authorization?.Scheme.Should().Be("Bearer");
            request.Content.Should().BeOfType<MultipartFormDataContent>();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            });
        }
    }

    private sealed class RecordingCatalogStore : IIngestorCatalogStore
    {
        public List<IngestorCatalogEntry> LoadedEntries { get; } = [];
        public IReadOnlyList<IngestorCatalogEntry> SavedEntries { get; private set; } = [];

        public IReadOnlyList<IngestorCatalogEntry> Load() => LoadedEntries;

        public void Save(IReadOnlyList<IngestorCatalogEntry> entries)
        {
            SavedEntries = entries;
        }
    }
}
