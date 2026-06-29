using System.Text.Json;
//using DocumentFormat.OpenXml.Drawing;
//using DocumentFormat.OpenXml.Packaging;
using DotnetRag.Ingestor.Models;
using DotnetRag.Ingestor.Services.ObjectStore;
using DotnetRag.Ingestor.Services.Telemetry;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Chunking;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Summarization;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DotnetRag.Ingestor.Services;

public sealed class IngestorService(
    InMemoryIngestorStore store,
    IngestionTaskHandler taskHandler,
    ILogger<IngestorService> logger,
    IVectorStoreClientFactory vectorStoreClientFactory,
    ISummarizationService summarizationService,
    IIngestionPipeline ingestionPipeline,
    IIngestionJobQueue jobQueue,
    IObjectStoreService objectStore,
    IIngestionTelemetrySink telemetry,
    RagServerConfiguration config)
{
    private readonly string _tempDir =
        Environment.GetEnvironmentVariable("TEMP_DIR") ?? Path.GetTempPath();

    public string DefaultCollectionName =>
        Environment.GetEnvironmentVariable("COLLECTION_NAME") ?? "multimodal_data";

    public async Task<object> UploadDocumentsAsync(
        HttpRequest request,
        IReadOnlyList<IFormFile> files,
        DocumentUploadRequest payload,
        bool isUpdate)
    {
        var summaryOptionsError = SummaryOptionsValidator.ValidateAndNormalize(
            payload.GenerateSummary,
            payload.SummaryOptions);
        if (summaryOptionsError is not null)
        {
            return new UploadValidationErrorResponse
            {
                Message = summaryOptionsError,
                TotalDocuments = files.Count
            };
        }

        var (allFilePaths, duplicateValidationErrors) = await ProcessFilePathsAsync(
            files,
            payload.CollectionName);
        var initialNvIngestStatus = IngestionTaskHandler.BuildInitialNvIngestStatus(allFilePaths);
        var vectorStoreClient = CreateVectorStoreClient(request, payload.VdbEndpoint);

        if (!payload.Blocking)
        {
            if (IsQueuedExecutionEnabled())
            {
                var queuedTaskId = Guid.NewGuid().ToString();
                taskHandler.SetPending(queuedTaskId, initialNvIngestStatus);
                await jobQueue.EnqueueAsync(new IngestionJob
                {
                    TaskId = queuedTaskId,
                    Payload = payload,
                    FilePaths = allFilePaths,
                    ValidationErrors = duplicateValidationErrors,
                    IsUpdate = isUpdate,
                    BearerToken = ExtractBearerToken(request)
                });

                return new IngestionTaskResponse
                {
                    Message = "Ingestion started in background",
                    TaskId = queuedTaskId
                };
            }

            var taskId = await taskHandler.SubmitTask(
                _ => ExecuteIngestionAsync(
                    payload,
                    allFilePaths,
                    duplicateValidationErrors,
                    isUpdate,
                    vectorStoreClient),
                initialNvIngestStatus);

            return new IngestionTaskResponse
            {
                Message = "Ingestion started in background",
                TaskId = taskId
            };
        }

        return await ExecuteIngestionAsync(
            payload,
            allFilePaths,
            duplicateValidationErrors,
            isUpdate,
            vectorStoreClient);
    }

    public Task<UploadDocumentResponse> ExecuteQueuedIngestionAsync(
        IngestionJob job,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var vectorStoreClient = vectorStoreClientFactory.Create(
            job.Payload.VdbEndpoint,
            job.BearerToken);
        return ExecuteIngestionAsync(
            job.Payload,
            job.FilePaths,
            job.ValidationErrors,
            job.IsUpdate,
            vectorStoreClient);
    }

    public Task<IngestionTaskStatusResponse> GetTaskStatusAsync(string taskId)
    {
        return Task.FromResult(taskHandler.GetTaskStatusAndResult(taskId));
    }

    public async Task<IngestorHealthResponse> HealthAsync(bool checkDependencies)
    {
        var response = new IngestorHealthResponse();
        if (!checkDependencies)
        {
            return response;
        }

        var vectorClient = vectorStoreClientFactory.Create();
        var vectorHealthy = await vectorClient.Management.CheckHealthAsync();
        response.Databases.Add(new Dictionary<string, object?>
        {
            ["service"] = config.VectorStoreName,
            ["url"] = config.VectorStoreUrl,
            ["status"] = vectorHealthy ? "healthy" : "unhealthy"
        });
        response.Processing.Add(new Dictionary<string, object?>
        {
            ["service"] = "ingestor",
            ["status"] = "healthy",
            ["backend"] = ingestionPipeline.BackendName,
            ["supports_multimodal_extraction"] = ingestionPipeline.SupportsMultimodalExtraction,
            ["supports_object_store_assets"] = ingestionPipeline.SupportsObjectStoreAssets
        });
        response.TaskManagement.Add(new Dictionary<string, object?>
        {
            ["service"] = "task_handler",
            ["status"] = "healthy",
            ["persistence"] = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APP_INGESTION_TASK_STORE_PATH"))
                ? "memory"
                : "file"
        });
        var objectStoreStatus = objectStore.IsEnabled
            ? await objectStore.CheckHealthAsync() ? "healthy" : "unhealthy"
            : "disabled";
        response.ObjectStorage.Add(new Dictionary<string, object?>
        {
            ["service"] = "object_storage",
            ["status"] = objectStoreStatus,
            ["backend"] = objectStore.BackendName
        });
        response.Nim.Add(new Dictionary<string, object?>
        {
            ["service"] = "model_services",
            ["status"] = "configured",
            ["embedding_provider"] = config.EmbeddingProvider,
            ["llm_provider"] = config.LlmProvider
        });

        return response;
    }

    public DocumentListResponse GetDocuments(
        HttpRequest request,
        string? collectionName,
        string? vdbEndpoint,
        bool forceGetMetadata,
        int maxResults)
    {
        _ = forceGetMetadata;

        var vectorClient = CreateVectorStoreClient(request, vdbEndpoint);
        var resolvedCollection = ResolveCollectionName(collectionName);
        EnsureCatalogCollectionFromVectorStore(resolvedCollection, vectorClient);
        var total = store.GetDocumentCount(resolvedCollection);
        var documents = store.GetDocuments(resolvedCollection, maxResults).ToList();
        return new DocumentListResponse
        {
            Message = "Document listing successfully completed.",
            TotalDocuments = total,
            Documents = documents
        };
    }

    public async Task<DocumentListResponse> DeleteDocumentsAsync(
        HttpRequest request,
        List<string> documentNames,
        string? collectionName,
        string? vdbEndpoint)
    {
        var vectorClient = CreateVectorStoreClient(request, vdbEndpoint);

        var resolvedCollection = ResolveCollectionName(collectionName);
        var existingNames = store.GetDocumentNames(resolvedCollection);
        var deleted = store.DeleteDocuments(resolvedCollection, documentNames);
        var notFound = documentNames.Count == 0
            ? new List<string>()
            : documentNames
                .Where(name => !existingNames.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (deleted.Count > 0)
        {
            var names = deleted.ToList();
            try
            {
                await vectorClient.Management.DeleteDocumentsAsync(resolvedCollection, names);
                await DeleteObjectStoreArtifactsAsync(resolvedCollection, names);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Vector-store document delete failed in collection '{Collection}'", resolvedCollection);
            }
        }

        return new DocumentListResponse
        {
            Message = notFound.Count == 0
                ? $"Deleted {deleted.Count} document(s)."
                : deleted.Count == 0
                    ? $"The following document(s) do not exist in the vectorstore: {string.Join(", ", notFound)}"
                    : $"Some documents deleted successfully. The following document(s) do not exist in the vectorstore: {string.Join(", ", notFound)}",
            TotalDocuments = deleted.Count,
            Documents = deleted.Select(name => new UploadedDocument
            {
                DocumentName = name,
                Metadata = [],
                DocumentInfo = []
            }).ToList()
        };
    }

    public CollectionListResponse GetCollections(HttpRequest request, string? vdbEndpoint)
    {
        var vectorClient = CreateVectorStoreClient(request, vdbEndpoint);
        var vectorCollections = LoadVectorCollectionDetails(vectorClient);
        EnsureCatalogCollectionsFromVectorStore(vectorClient, vectorCollections);
        var collections = store.GetCollections()
            .Where(collection => !IsAuxiliaryCollectionName(collection.CollectionName))
            .ToList();
        MergeVectorCollectionDetails(collections, vectorCollections);

        return new CollectionListResponse
        {
            Message = "Collections listed successfully.",
            Collections = collections,
            TotalCollections = collections.Count
        };
    }

    public CollectionsResponse CreateCollections(
        HttpRequest request,
        string? vdbEndpoint,
        List<string> collectionNames)
    {
        var vectorClient = CreateVectorStoreClient(request, vdbEndpoint);
        var successful = new List<string>();
        var failed = new List<FailedCollection>();

        foreach (var collectionName in collectionNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var created = store.CreateCollection(new CreateCollectionRequest
            {
                CollectionName = collectionName
            });
            if (created)
            {
                successful.Add(collectionName);
                try
                {
                    vectorClient.Management.EnsureCollectionAsync(collectionName).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Vector-store collection sync failed for '{Collection}'", collectionName);
                }
            }
            else
            {
                failed.Add(new FailedCollection
                {
                    CollectionName = collectionName,
                    ErrorMessage = $"Collection {collectionName} already exists."
                });
            }
        }

        return new CollectionsResponse
        {
            Message = "Collection creation process completed.",
            Successful = successful,
            Failed = failed,
            TotalSuccess = successful.Count,
            TotalFailed = failed.Count
        };
    }

    public async Task<CreateCollectionResponse> CreateCollectionAsync(
        HttpRequest httpRequest,
        CreateCollectionRequest request)
    {
        var created = store.CreateCollection(request);
        var vectorClient = CreateVectorStoreClient(httpRequest, request.VdbEndpoint);

        try
        {
            await vectorClient.Management.EnsureCollectionAsync(request.CollectionName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Vector-store collection sync failed for '{Collection}'", request.CollectionName);
        }

        return new CreateCollectionResponse
        {
            Message = created
                ? $"Collection {request.CollectionName} created successfully."
                : $"Collection {request.CollectionName} already exists.",
            CollectionName = request.CollectionName
        };
    }

    public UpdateMetadataResponse UpdateCollectionMetadata(
        string collectionName,
        UpdateCollectionMetadataRequest request)
    {
        var updated = store.UpdateCollectionMetadata(collectionName, request);
        if (!updated)
        {
            return new UpdateMetadataResponse
            {
                Message = $"Collection {collectionName} does not exist",
                CollectionName = collectionName
            };
        }

        return new UpdateMetadataResponse
        {
            Message = $"Collection {collectionName} metadata updated successfully.",
            CollectionName = collectionName
        };
    }

    public UpdateMetadataResponse UpdateDocumentMetadata(
        string collectionName,
        string documentName,
        UpdateDocumentMetadataRequest request)
    {
        var updated = store.UpdateDocumentMetadata(collectionName, documentName, request);
        if (!updated)
        {
            return new UpdateMetadataResponse
            {
                Message =
                    $"Document '{documentName}' does not exist in collection '{collectionName}'",
                CollectionName = collectionName
            };
        }

        return new UpdateMetadataResponse
        {
            Message = $"Document metadata updated successfully for {documentName}.",
            CollectionName = collectionName
        };
    }

    public async Task<CollectionsResponse> DeleteCollectionsAsync(
        HttpRequest request,
        List<string> collectionNames,
        string? vdbEndpoint)
    {
        var vectorClient = CreateVectorStoreClient(request, vdbEndpoint);

        var successful = new List<string>();
        var failed = new List<FailedCollection>();

        foreach (var collectionName in collectionNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (store.DeleteCollection(collectionName))
            {
                successful.Add(collectionName);
                var name = collectionName;
                try
                {
                    DeleteOwnedCatalogCollections(name);
                    await DeleteOwnedVectorCollectionsAsync(vectorClient, name);
                    await DeleteOwnedObjectStoreArtifactsAsync(name);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Vector-store delete failed for collection '{Collection}'", name);
                }
                continue;
            }

            failed.Add(new FailedCollection
            {
                CollectionName = collectionName,
                ErrorMessage = $"Collection {collectionName} does not exist."
            });
        }

        return new CollectionsResponse
        {
            Message = "Collection deletion process completed.",
            Successful = successful,
            Failed = failed,
            TotalSuccess = successful.Count,
            TotalFailed = failed.Count
        };
    }

    private async Task<UploadDocumentResponse> ExecuteIngestionAsync(
        DocumentUploadRequest payload,
        List<string> allFilePaths,
        List<Dictionary<string, object?>> duplicateValidationErrors,
        bool isUpdate,
        VectorStoreClient vectorStoreClient)
    {
        var resolvedCollection = ResolveCollectionName(payload.CollectionName);
        var startedAt = DateTimeOffset.UtcNow;
        telemetry.Checkpoint("ingestion.started", new Dictionary<string, object?>
        {
            ["collection_name"] = resolvedCollection,
            ["total_documents"] = allFilePaths.Count,
            ["blocking"] = payload.Blocking,
            ["is_update"] = isUpdate,
            ["ingestion_backend"] = ingestionPipeline.BackendName
        });

        if (!store.CollectionExists(resolvedCollection))
        {
            store.CreateCollection(new CreateCollectionRequest
            {
                CollectionName = resolvedCollection
            });
            await vectorStoreClient.Management.EnsureCollectionAsync(resolvedCollection);
        }

        var existingDocumentNames = store.GetDocumentNames(resolvedCollection);
        var failed = new List<FailedDocument>();
        var succeeded = new List<InMemoryIngestorStore.StoredDocument>();
        var extractedTextByDocument = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var catalogMetadataByName = payload.DocumentsCatalogMetadata.ToDictionary(
            item => item.Filename,
            item => item,
            StringComparer.OrdinalIgnoreCase);

        foreach (var path in allFilePaths)
        {
            var name = Path.GetFileName(path);
            if (!ingestionPipeline.SupportsFile(name))
            {
                failed.Add(new FailedDocument
                {
                    DocumentName = name,
                    ErrorMessage =
                        "Unsupported file type, supported file types are: "
                        + ingestionPipeline.SupportedFileTypesMessage
                });
                continue;
            }

            if (!isUpdate && existingDocumentNames.Contains(name))
            {
                failed.Add(new FailedDocument
                {
                    DocumentName = name,
                    ErrorMessage = $"Document {name} already exists. Use update document API instead."
                });
                continue;
            }

            var metadata = payload.CustomMetadata
                .FirstOrDefault(item => string.Equals(item.Filename, name, StringComparison.OrdinalIgnoreCase))
                ?.Metadata ?? new Dictionary<string, object?>();

            var metadataValidation = store.ValidateAndNormalizeDocumentMetadata(
                resolvedCollection,
                name,
                metadata);
            foreach (var error in metadataValidation.Errors)
            {
                duplicateValidationErrors.Add(new Dictionary<string, object?>
                {
                    ["error"] = error,
                    ["metadata"] = new Dictionary<string, object?> { ["filename"] = name }
                });
            }
            if (!metadataValidation.IsValid)
            {
                failed.Add(new FailedDocument
                {
                    DocumentName = name,
                    ErrorMessage = $"Metadata validation failed for {name}"
                });
                continue;
            }

            metadata = metadataValidation.NormalizedMetadata;

            IngestionPipelineResult pipelineResult;
            try
            {
                pipelineResult = await ingestionPipeline.ExtractAsync(
                    path,
                    name,
                    payload.ExtractionOptions);
            }
            catch (Exception ex)
            {
                failed.Add(new FailedDocument
                {
                    DocumentName = name,
                    ErrorMessage = ex.Message
                });
                telemetry.Checkpoint("ingestion.document_failed", new Dictionary<string, object?>
                {
                    ["collection_name"] = resolvedCollection,
                    ["document_name"] = name,
                    ["error"] = ex.Message
                });
                continue;
            }
            var rawText = pipelineResult.Text;
            extractedTextByDocument[name] = rawText;
            var fileSize = new FileInfo(path).Length;

            var documentInfo = new Dictionary<string, object?>
            {
                ["document_id"] = Guid.NewGuid().ToString(),
                ["upload_path"] = path,
                ["document_type"] = Path.GetExtension(name).TrimStart('.').ToLowerInvariant(),
                ["file_size"] = fileSize,
                ["size_bytes"] = fileSize,
                ["date_created"] = DateTimeOffset.UtcNow.ToString("O"),
                ["last_indexed"] = DateTimeOffset.UtcNow.ToString("O"),
                ["ingestion_backend"] = ingestionPipeline.BackendName,
                ["ingestion_status"] = "completed",
                ["total_elements"] = EstimateElementCount(rawText),
                ["raw_text_elements_size"] = rawText.Length,
                ["has_tables"] = false,
                ["has_charts"] = false,
                ["has_images"] = false
            };
            foreach (var kv in pipelineResult.DocumentInfo)
            {
                documentInfo[kv.Key] = kv.Value;
            }
            if (pipelineResult.AssetObjectNames.Count > 0)
            {
                documentInfo["asset_object_names"] = pipelineResult.AssetObjectNames;
            }

            if (catalogMetadataByName.TryGetValue(name, out var catalog))
            {
                if (!string.IsNullOrWhiteSpace(catalog.Description))
                {
                    documentInfo["description"] = catalog.Description;
                }

                if (catalog.Tags is { Count: > 0 })
                {
                    documentInfo["tags"] = catalog.Tags;
                }
            }

            succeeded.Add(new InMemoryIngestorStore.StoredDocument
            {
                DocumentName = name,
                Metadata = metadata,
                DocumentInfo = documentInfo
            });
            telemetry.Checkpoint("ingestion.document_validated", new Dictionary<string, object?>
            {
                ["collection_name"] = resolvedCollection,
                ["document_name"] = name,
                ["document_type"] = documentInfo["document_type"],
                ["file_size"] = documentInfo["file_size"]
            });
        }

        store.UpsertDocuments(resolvedCollection, succeeded, replaceExisting: isUpdate);

        await IngestIntoVectorStoreAsync(
            resolvedCollection,
            succeeded,
            isUpdate,
            vectorStoreClient,
            payload.SplitOptions,
            extractedTextByDocument);
        await StoreCitationAssetsAsync(resolvedCollection, succeeded);

        // ORIG: nvidia_rag/ingestor_server/main.py::__run_background_ingest_task — generate_summary branch
        if (payload.GenerateSummary && succeeded.Count > 0)
        {
            var summaryDocs = await PrepareSummaryDocumentsAsync(
                succeeded,
                payload.SummaryOptions?.PageFilter,
                extractedTextByDocument);

            var strategy = ParseSummarizationStrategy(payload.SummaryOptions?.SummarizationStrategy);
            var summaryOptions = new SummarizationOptions(
                Strategy: strategy,
                PageFilter: payload.SummaryOptions?.PageFilter,
                IsShallow: payload.SummaryOptions?.ShallowSummary ?? false);

            await summarizationService.GenerateDocumentSummariesAsync(
                summaryDocs, resolvedCollection, summaryOptions);
        }

        await Task.Yield();

        var duration = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;
        telemetry.Checkpoint("ingestion.completed", new Dictionary<string, object?>
        {
            ["collection_name"] = resolvedCollection,
            ["total_documents"] = allFilePaths.Count,
            ["documents_completed"] = succeeded.Count,
            ["failed_documents"] = failed.Count,
            ["duration_seconds"] = duration
        });

        return new UploadDocumentResponse
        {
            Message = "Document upload job successfully completed.",
            TotalDocuments = allFilePaths.Count,
            DocumentsCompleted = succeeded.Count,
            BatchesCompleted = succeeded.Count > 0 ? 1 : 0,
            Documents = succeeded.Select(item => item.ToUploadedDocument()).ToList(),
            FailedDocuments = failed,
            ValidationErrors = duplicateValidationErrors
        };
    }

    private async Task<(List<string> FilePaths, List<Dictionary<string, object?>> ValidationErrors)>
        ProcessFilePathsAsync(
            IReadOnlyList<IFormFile> files,
            string collectionName)
    {
        var baseUploadFolder = Path.Combine(
            _tempDir,
            "uploaded_files",
            ToSafePathSegment(collectionName));
        Directory.CreateDirectory(baseUploadFolder);

        var filePaths = new List<string>();
        var filenameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var name = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            filenameCounts[name] = filenameCounts.GetValueOrDefault(name) + 1;
            if (!processed.Add(name))
            {
                continue;
            }

            var filePath = Path.Combine(baseUploadFolder, name);
            await using var stream = File.Create(filePath);
            await file.CopyToAsync(stream);
            filePaths.Add(filePath);
        }

        var validationErrors = new List<Dictionary<string, object?>>();
        foreach (var duplicate in filenameCounts.Where(pair => pair.Value > 1))
        {
            var duplicateCount = duplicate.Value - 1;
            validationErrors.Add(new Dictionary<string, object?>
            {
                ["error"] =
                    $"File '{duplicate.Key}': Total of {duplicateCount} duplicate(s) found. Duplicates were discarded and 1 file is being processed.",
                ["metadata"] = new Dictionary<string, object?>
                {
                    ["filename"] = duplicate.Key,
                    ["duplicate_count"] = duplicateCount,
                    ["total_occurrences"] = duplicate.Value
                }
            });
        }

        logger.LogInformation(
            "Prepared {FileCount} unique file(s) for ingestion into collection {CollectionName}.",
            filePaths.Count,
            collectionName);

        return (filePaths, validationErrors);
    }

    private static string ToSafePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "default";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => ch == Path.DirectorySeparatorChar
                || ch == Path.AltDirectorySeparatorChar
                || invalid.Contains(ch)
                    ? '_'
                    : ch)
            .ToArray();

        var safe = new string(chars).Trim('.');
        return string.IsNullOrWhiteSpace(safe) ? "default" : safe;
    }

    private async Task IngestIntoVectorStoreAsync(
        string collectionName,
        IReadOnlyList<InMemoryIngestorStore.StoredDocument> documents,
        bool isUpdate,
        VectorStoreClient vectorStoreClient,
        SplitOptions splitOptions,
        IReadOnlyDictionary<string, string> extractedTextByDocument)
    {
        await vectorStoreClient.Management.EnsureCollectionAsync(collectionName);

        if (isUpdate)
        {
            var docNames = documents.Select(d => d.DocumentName).ToList();
            await vectorStoreClient.Management.DeleteDocumentsAsync(collectionName, docNames);
            await vectorStoreClient.Management.CompactCollectionAsync(collectionName);
        }

        var allChunks = new List<VectorDocument>();

        foreach (var doc in documents)
        {
            var uploadPath = doc.DocumentInfo.TryGetValue("upload_path", out var p) ? p?.ToString() : null;
            if (uploadPath is null || !File.Exists(uploadPath))
            {
                logger.LogWarning("Skipping vector-store upsert for '{Doc}': file not found at '{Path}'", doc.DocumentName, uploadPath);
                continue;
            }

            var text = extractedTextByDocument.TryGetValue(doc.DocumentName, out var cachedText)
                ? cachedText
                : await ExtractTextForDocumentAsync(uploadPath, doc.DocumentName);

            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogWarning("No text extracted from '{Doc}', skipping.", doc.DocumentName);
                continue;
            }

            var ext = Path.GetExtension(doc.DocumentName).ToLowerInvariant();
            var chunkSize = splitOptions.ChunkSize > 0 ? splitOptions.ChunkSize : config.ChunkSize;
            var chunkOverlap = splitOptions.ChunkOverlap >= 0 ? splitOptions.ChunkOverlap : config.ChunkOverlap;
            var chunks = ext == ".md"
                ? DocumentChunker.ChunkMarkdown(text, maxTokensPerParagraph: chunkSize, overlapTokens: chunkOverlap)
                : DocumentChunker.ChunkText(text, maxTokensPerParagraph: chunkSize, overlapTokens: chunkOverlap);

            var baseMeta = BuildVectorMetadata(
                doc.Metadata,
                doc.DocumentInfo,
                doc.DocumentName,
                collectionName);

            for (int i = 0; i < chunks.Count; i++)
            {
                allChunks.Add(new VectorDocument(
                    Id: $"{doc.DocumentName}__chunk_{i}",
                    Text: chunks[i],
                    Metadata: baseMeta));
            }
        }

        if (allChunks.Count > 0)
        {
            await vectorStoreClient.Store.UpsertAsync(collectionName, allChunks);
            telemetry.Checkpoint("ingestion.vector_upserted", new Dictionary<string, object?>
            {
                ["collection_name"] = collectionName,
                ["chunk_count"] = allChunks.Count,
                ["document_count"] = documents.Count,
                ["vector_store"] = config.VectorStoreName
            });
            logger.LogInformation(
                "Upserted {ChunkCount} chunk(s) from {DocCount} document(s) into vector-store collection '{Collection}'",
                allChunks.Count,
                documents.Count,
                collectionName);
        }
    }

    public static Dictionary<string, object?> BuildVectorMetadata(
        IReadOnlyDictionary<string, object?> documentMetadata,
        IReadOnlyDictionary<string, object?> documentInfo,
        string documentName,
        string collectionName)
    {
        var metadata = documentMetadata.ToDictionary(kv => kv.Key, kv => kv.Value);
        metadata["filename"] = documentName;
        metadata["collection_name"] = collectionName;

        CopyDocumentInfoField(metadata, documentInfo, "document_type", "type");
        CopyDocumentInfoField(metadata, documentInfo, "page_number", "page_number");
        CopyDocumentInfoField(metadata, documentInfo, "source_location", "source_location");
        CopyDocumentInfoField(metadata, documentInfo, "stored_image_uri", "stored_image_uri");
        CopyDocumentInfoField(metadata, documentInfo, "image_uri", "image_uri");
        CopyDocumentInfoField(metadata, documentInfo, "asset_uri", "asset_uri");

        if (!metadata.ContainsKey("source_location")
            && TryGetFirstString(documentInfo, "asset_object_names", out var firstAssetObjectName))
        {
            metadata["source_location"] = firstAssetObjectName;
            metadata["stored_image_uri"] = firstAssetObjectName;
        }

        if (!metadata.ContainsKey("content_metadata.type") && metadata.TryGetValue("type", out var type))
        {
            metadata["content_metadata.type"] = type;
        }

        if (!metadata.ContainsKey("content_metadata.page_number") && metadata.TryGetValue("page_number", out var pageNumber))
        {
            metadata["content_metadata.page_number"] = pageNumber;
        }

        if (documentInfo.TryGetValue("content_metadata", out var contentMetadata)
            && contentMetadata is IReadOnlyDictionary<string, object?> contentMetadataDictionary)
        {
            foreach (var (key, value) in contentMetadataDictionary)
            {
                metadata[$"content_metadata.{key}"] = ToVectorMetadataValue(value);
            }
        }
        else if (contentMetadata is IDictionary<string, object?> mutableContentMetadata)
        {
            foreach (var (key, value) in mutableContentMetadata)
            {
                metadata[$"content_metadata.{key}"] = ToVectorMetadataValue(value);
            }
        }

        return metadata;
    }

    private static void CopyDocumentInfoField(
        IDictionary<string, object?> metadata,
        IReadOnlyDictionary<string, object?> documentInfo,
        string sourceKey,
        string targetKey)
    {
        if (metadata.ContainsKey(targetKey)
            || !documentInfo.TryGetValue(sourceKey, out var value)
            || value is null)
        {
            return;
        }

        metadata[targetKey] = ToVectorMetadataValue(value);
    }

    private static object? ToVectorMetadataValue(object? value)
    {
        return value switch
        {
            null => null,
            string or bool or int or long or float or double or decimal => value,
            _ => JsonSerializer.Serialize(value)
        };
    }

    private static bool TryGetFirstString(
        IReadOnlyDictionary<string, object?> documentInfo,
        string key,
        out string value)
    {
        value = string.Empty;
        if (!documentInfo.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        if (raw is IEnumerable<string> strings)
        {
            value = strings.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        if (raw is IEnumerable<object?> objects)
        {
            value = objects
                .Select(item => item?.ToString())
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    private static int EstimateElementCount(string text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split(
                    ['\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Length;
    }

    private string ResolveCollectionName(string? collectionName)
    {
        return string.IsNullOrWhiteSpace(collectionName)
            ? DefaultCollectionName
            : collectionName;
    }

    private VectorStoreClient CreateVectorStoreClient(HttpRequest request, string? endpoint)
    {
        return vectorStoreClientFactory.Create(endpoint, ExtractBearerToken(request));
    }

    private static bool IsQueuedExecutionEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable("APP_INGESTION_EXECUTION_MODE"),
            "queued",
            StringComparison.OrdinalIgnoreCase);

    private static string? ExtractBearerToken(HttpRequest request)
    {
        var authHeader = request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        return authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? authHeader[bearerPrefix.Length..].Trim()
            : null;
    }

    private void EnsureCatalogCollectionFromVectorStore(
        string collectionName,
        VectorStoreClient vectorClient)
    {
        if (store.CollectionExists(collectionName))
        {
            return;
        }

        try
        {
            if (vectorClient.Management.CollectionExistsAsync(collectionName).GetAwaiter().GetResult())
            {
                store.CreateCollection(new CreateCollectionRequest { CollectionName = collectionName });
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Vector-store collection existence check failed for '{Collection}'.", collectionName);
        }
    }

    private IReadOnlyDictionary<string, VectorStoreCollectionDetails> LoadVectorCollectionDetails(
        VectorStoreClient vectorClient)
    {
        try
        {
            return vectorClient.Management.ListCollectionsAsync()
                .GetAwaiter()
                .GetResult()
                .Where(item => !IsAuxiliaryCollectionName(item.CollectionName))
                .GroupBy(item => item.CollectionName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Vector-store collection details listing failed.");
            return new Dictionary<string, VectorStoreCollectionDetails>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void EnsureCatalogCollectionsFromVectorStore(
        VectorStoreClient vectorClient,
        IReadOnlyDictionary<string, VectorStoreCollectionDetails> vectorCollections)
    {
        try
        {
            foreach (var collectionName in vectorCollections.Keys)
            {
                if (!store.CollectionExists(collectionName))
                {
                    store.CreateCollection(new CreateCollectionRequest { CollectionName = collectionName });
                }
            }

            var defaultCollection = DefaultCollectionName;
            if (store.CollectionExists(defaultCollection)
                || vectorCollections.ContainsKey(defaultCollection))
            {
                return;
            }

            if (vectorClient.Management.CollectionExistsAsync(defaultCollection).GetAwaiter().GetResult())
            {
                store.CreateCollection(new CreateCollectionRequest { CollectionName = defaultCollection });
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Vector-store catalog bootstrap check failed.");
        }
    }

    private static void MergeVectorCollectionDetails(
        List<UploadedCollection> collections,
        IReadOnlyDictionary<string, VectorStoreCollectionDetails> vectorCollections)
    {
        if (vectorCollections.Count == 0)
        {
            return;
        }

        foreach (var collection in collections)
        {
            if (!vectorCollections.TryGetValue(collection.CollectionName, out var vectorCollection))
            {
                continue;
            }

            collection.NumEntities = ToInt32Count(vectorCollection.NumEntities);

            if (collection.MetadataSchema.Count == 0 && vectorCollection.MetadataSchema.Count > 0)
            {
                collection.MetadataSchema = vectorCollection.MetadataSchema
                    .Select(item => item.ToDictionary(kv => kv.Key, kv => kv.Value))
                    .ToList();
            }

            foreach (var item in vectorCollection.CollectionInfo)
            {
                collection.CollectionInfo.TryAdd(item.Key, item.Value);
            }
        }
    }

    private static int ToInt32Count(long value) =>
        value > int.MaxValue
            ? int.MaxValue
            : value < 0
                ? 0
                : (int)value;

    private async Task DeleteOwnedVectorCollectionsAsync(
        VectorStoreClient vectorClient,
        string collectionName)
    {
        await vectorClient.Management.DeleteCollectionAsync(collectionName);

        foreach (var auxiliaryCollection in GetAuxiliaryCollectionNames(collectionName))
        {
            try
            {
                if (await vectorClient.Management.CollectionExistsAsync(auxiliaryCollection))
                {
                    await vectorClient.Management.DeleteCollectionAsync(auxiliaryCollection);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Vector-store delete failed for auxiliary collection '{Collection}' owned by '{OwnerCollection}'",
                    auxiliaryCollection,
                    collectionName);
            }
        }
    }

    private void DeleteOwnedCatalogCollections(string collectionName)
    {
        foreach (var auxiliaryCollection in GetAuxiliaryCollectionNames(collectionName))
        {
            store.DeleteCollection(auxiliaryCollection);
        }
    }

    private async Task StoreCitationAssetsAsync(
        string collectionName,
        IReadOnlyList<InMemoryIngestorStore.StoredDocument> documents)
    {
        if (!objectStore.IsEnabled)
        {
            return;
        }

        foreach (var document in documents)
        {
            await objectStore.StoreJsonAsync(
                collectionName,
                $"{document.DocumentName}/citation.json",
                new Dictionary<string, object?>
                {
                    ["filename"] = document.DocumentName,
                    ["metadata"] = document.Metadata,
                    ["document_info"] = document.DocumentInfo
                });

            if (document.DocumentInfo.TryGetValue("asset_object_names", out var assets)
                && assets is not null)
            {
                await objectStore.StoreJsonAsync(
                    collectionName,
                    $"{document.DocumentName}/asset_manifest.json",
                    new Dictionary<string, object?>
                    {
                        ["filename"] = document.DocumentName,
                        ["asset_object_names"] = assets
                    });
            }
        }

        telemetry.Checkpoint("ingestion.object_store_citations_written", new Dictionary<string, object?>
        {
            ["collection_name"] = collectionName,
            ["document_count"] = documents.Count,
            ["backend"] = objectStore.BackendName
        });
    }

    private async Task DeleteObjectStoreArtifactsAsync(
        string collectionName,
        IReadOnlyList<string> documentNames)
    {
        if (!objectStore.IsEnabled)
        {
            return;
        }

        foreach (var documentName in documentNames)
        {
            var citationObjects = await objectStore.ListAsync(collectionName, $"{documentName}/");
            await objectStore.DeleteAsync(collectionName, citationObjects);

            var summaryObjects = await objectStore.ListAsync($"summary_{collectionName}", documentName);
            await objectStore.DeleteAsync($"summary_{collectionName}", summaryObjects);
        }
    }

    private async Task DeleteOwnedObjectStoreArtifactsAsync(string collectionName)
    {
        await DeleteCollectionObjectStoreArtifactsAsync(collectionName);
        foreach (var auxiliaryCollection in GetAuxiliaryCollectionNames(collectionName))
        {
            await DeleteCollectionObjectStoreArtifactsAsync(auxiliaryCollection);
        }
    }

    private static IReadOnlyList<string> GetAuxiliaryCollectionNames(string collectionName)
    {
        if (IsAuxiliaryCollectionName(collectionName))
        {
            return [];
        }

        return [$"summary_{collectionName}"];
    }

    private static bool IsAuxiliaryCollectionName(string collectionName) =>
        collectionName.StartsWith("summary_", StringComparison.OrdinalIgnoreCase);

    private async Task DeleteCollectionObjectStoreArtifactsAsync(string collectionName)
    {
        if (!objectStore.IsEnabled)
        {
            return;
        }

        var citationObjects = await objectStore.ListAsync(collectionName, string.Empty);
        await objectStore.DeleteAsync(collectionName, citationObjects);
    }

    // ── Summarization helpers ─────────────────────────────────────────────────

    // ORIG: nvidia_rag/utils/summarization.py::_prepare_single_document
    // Applies page filter for PDFs/PPTX; falls back to full text for other formats.
    private async Task<IReadOnlyList<DocumentContent>> PrepareSummaryDocumentsAsync(
        IReadOnlyList<InMemoryIngestorStore.StoredDocument> documents,
        object? pageFilter,
        IReadOnlyDictionary<string, string> extractedTextByDocument)
    {
        var result = new List<DocumentContent>(documents.Count);

        foreach (var doc in documents)
        {
            var path = doc.DocumentInfo.TryGetValue("upload_path", out var p) ? p?.ToString() : null;
            if (path is null || !File.Exists(path))
            {
                continue;
            }

            var ext = Path.GetExtension(doc.DocumentName).ToLowerInvariant();
            string text;

            if (pageFilter is not null && ext is ".pdf" or ".pptx")
            {
                // Page-level extraction + filter
                var pages = ext == ".pdf"
                    ? ExtractPdfPages(path)
                    : ExtractPptxPages(path);
                var totalPages = pages.Count;
                text = string.Join(" ", pages
                    .Where(pg => PageFilter.Matches(pg.Page, pageFilter, totalPages))
                    .Select(pg => pg.Text));
            }
            else
            {
                text = extractedTextByDocument.TryGetValue(doc.DocumentName, out var cachedText)
                    ? cachedText
                    : await ExtractTextForDocumentAsync(path, doc.DocumentName);
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                result.Add(new DocumentContent(doc.DocumentName, text));
            }
        }

        return result;
    }

    private async Task<string> ExtractTextForDocumentAsync(string uploadPath, string documentName)
    {
        try
        {
            return await ingestionPipeline.ExtractTextAsync(uploadPath, documentName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not extract text from '{Path}', skipping.", uploadPath);
            return string.Empty;
        }
    }

    private static IReadOnlyList<(int Page, string Text)> ExtractPdfPages(string path)
    {
        using var document = PdfDocument.Open(path);
        return document.GetPages()
            .Select((page, i) => (i + 1, ContentOrderTextExtractor.GetText(page)))
            .Where(p => !string.IsNullOrWhiteSpace(p.Item2))
            .ToList();
    }

    private static IReadOnlyList<(int Page, string Text)> ExtractPptxPages(string path)
    {
        using var prs = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(path, false);
        return (prs.PresentationPart?.SlideParts ?? [])
            .Select((slidePart, i) =>
            {
                var texts = slidePart.Slide
                    .Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                    .Select(t => t.Text);
                return (i + 1, string.Join(" ", texts));
            })
            .Where(p => !string.IsNullOrWhiteSpace(p.Item2))
            .ToList();
    }

    private static SummarizationStrategy ParseSummarizationStrategy(string? strategy) =>
        strategy?.Trim().ToLowerInvariant() switch
        {
            "single" => SummarizationStrategy.Single,
            "hierarchical" => SummarizationStrategy.Hierarchical,
            _ => SummarizationStrategy.Iterative
        };
}
