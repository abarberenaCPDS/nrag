using System.Text;
using System.Text.RegularExpressions;
//using DocumentFormat.OpenXml.Drawing;
//using DocumentFormat.OpenXml.Packaging;
using DotnetRag.Ingestor.Models;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Chunking;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Summarization;
using DotnetRag.Shared.VectorStore;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DotnetRag.Ingestor.Services;

public sealed class IngestorService(
    InMemoryIngestorStore store,
    IngestionTaskHandler taskHandler,
    ILogger<IngestorService> logger,
    IVectorStore vectorStore,
    ChromaDbVectorStore chromaStore,
    ISummarizationService summarizationService,
    RagServerConfiguration config)
{
    private static readonly string[] UnsupportedFormats = [".rst", ".rtf", ".org", ".svg"];
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);

    private readonly string _tempDir =
        Environment.GetEnvironmentVariable("TEMP_DIR") ?? Path.GetTempPath();

    public string DefaultCollectionName =>
        Environment.GetEnvironmentVariable("COLLECTION_NAME") ?? "multimodal_data";

    public Task<IngestorHealthResponse> HealthAsync(bool checkDependencies)
    {
        var response = new IngestorHealthResponse();
        if (checkDependencies)
        {
            response.Processing.Add(new Dictionary<string, object?>
            {
                ["service"] = "ingestor",
                ["status"] = "healthy"
            });
            response.TaskManagement.Add(new Dictionary<string, object?>
            {
                ["service"] = "task_handler",
                ["status"] = "healthy"
            });
        }

        return Task.FromResult(response);
    }

    public async Task<object> UploadDocumentsAsync(
        HttpRequest request,
        IReadOnlyList<IFormFile> files,
        DocumentUploadRequest payload,
        bool isUpdate)
    {
        if (payload.SummaryOptions is not null && !payload.GenerateSummary)
        {
            return new UploadDocumentResponse
            {
                Message = "summary_options can only be provided when generate_summary=True.",
                TotalDocuments = files.Count
            };
        }

        var (allFilePaths, duplicateValidationErrors) = await ProcessFilePathsAsync(
            files,
            payload.CollectionName);
        var initialNvIngestStatus = IngestionTaskHandler.BuildInitialNvIngestStatus(allFilePaths);

        if (!payload.Blocking)
        {
            var taskId = await taskHandler.SubmitTask(
                _ => ExecuteIngestionAsync(
                    request,
                    payload,
                    allFilePaths,
                    duplicateValidationErrors,
                    isUpdate),
                initialNvIngestStatus);

            return new IngestionTaskResponse
            {
                Message = "Ingestion started in background",
                TaskId = taskId
            };
        }

        return await ExecuteIngestionAsync(
            request,
            payload,
            allFilePaths,
            duplicateValidationErrors,
            isUpdate);
    }

    public Task<IngestionTaskStatusResponse> GetTaskStatusAsync(string taskId)
    {
        return Task.FromResult(taskHandler.GetTaskStatusAndResult(taskId));
    }

    public DocumentListResponse GetDocuments(
        HttpRequest request,
        string? collectionName,
        string? vdbEndpoint,
        bool forceGetMetadata,
        int maxResults)
    {
        _ = request;
        _ = vdbEndpoint;
        _ = forceGetMetadata;

        var resolvedCollection = ResolveCollectionName(collectionName);
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
        _ = request;
        _ = vdbEndpoint;

        var resolvedCollection = ResolveCollectionName(collectionName);
        var deleted = store.DeleteDocuments(resolvedCollection, documentNames);

        if (deleted.Count > 0)
        {
            var names = deleted.ToList();
            try
            {
                await chromaStore.DeleteDocumentsAsync(resolvedCollection, names);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ChromaDB document delete failed in collection '{Collection}'", resolvedCollection);
            }
        }

        return new DocumentListResponse
        {
            Message = $"Deleted {deleted.Count} document(s).",
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
        _ = request;
        _ = vdbEndpoint;
        var collections = store.GetCollections().ToList();
        return new CollectionListResponse
        {
            Message = "Collections listed successfully.",
            Collections = collections,
            TotalCollections = collections.Count
        };
    }

    public CollectionsResponse CreateCollections(
        string? vdbEndpoint,
        List<string> collectionNames)
    {
        _ = vdbEndpoint;
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

    public async Task<CreateCollectionResponse> CreateCollectionAsync(CreateCollectionRequest request)
    {
        var created = store.CreateCollection(request);

        try
        {
            await chromaStore.EnsureCollectionAsync(request.CollectionName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ChromaDB collection sync failed for '{Collection}'", request.CollectionName);
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
        _ = request;
        _ = vdbEndpoint;

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
                    await chromaStore.DeleteCollectionAsync(name);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "ChromaDB delete failed for collection '{Collection}'", name);
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
        HttpRequest request,
        DocumentUploadRequest payload,
        List<string> allFilePaths,
        List<Dictionary<string, object?>> duplicateValidationErrors,
        bool isUpdate)
    {
        _ = request;
        var resolvedCollection = ResolveCollectionName(payload.CollectionName);
        if (!store.CollectionExists(resolvedCollection))
        {
            return new UploadDocumentResponse
            {
                Message =
                    $"Collection {resolvedCollection} does not exist. Ensure a collection is created using POST /collection endpoint first.",
                TotalDocuments = allFilePaths.Count,
                FailedDocuments = allFilePaths.Select(path => new FailedDocument
                {
                    DocumentName = Path.GetFileName(path),
                    ErrorMessage = "Collection does not exist."
                }).ToList(),
                ValidationErrors = duplicateValidationErrors
            };
        }

        var existingDocumentNames = store.GetDocumentNames(resolvedCollection);
        var failed = new List<FailedDocument>();
        var succeeded = new List<InMemoryIngestorStore.StoredDocument>();
        var catalogMetadataByName = payload.DocumentsCatalogMetadata.ToDictionary(
            item => item.Filename,
            item => item,
            StringComparer.OrdinalIgnoreCase);

        foreach (var path in allFilePaths)
        {
            var name = Path.GetFileName(path);
            if (UnsupportedFormats.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            {
                failed.Add(new FailedDocument
                {
                    DocumentName = name,
                    ErrorMessage = "Unsupported file type, supported file types are txt, md, pdf, docx, pptx, html."
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

            // Schema validation: warn on missing required fields or type mismatches
            var schemaErrors = store.ValidateDocumentMetadata(resolvedCollection, name, metadata);
            foreach (var error in schemaErrors)
            {
                duplicateValidationErrors.Add(new Dictionary<string, object?>
                {
                    ["error"] = error,
                    ["metadata"] = new Dictionary<string, object?> { ["filename"] = name }
                });
            }

            var documentInfo = new Dictionary<string, object?>
            {
                ["upload_path"] = path
            };

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
        }

        store.UpsertDocuments(resolvedCollection, succeeded, replaceExisting: isUpdate);

        await IngestIntoVectorStoreAsync(resolvedCollection, succeeded, isUpdate);

        // ORIG: nvidia_rag/ingestor_server/main.py::__run_background_ingest_task — generate_summary branch
        if (payload.GenerateSummary && succeeded.Count > 0)
        {
            var summaryDocs = await PrepareSummaryDocumentsAsync(
                succeeded, payload.SummaryOptions?.PageFilter);

            var strategy = ParseSummarizationStrategy(payload.SummaryOptions?.SummarizationStrategy);
            var summaryOptions = new SummarizationOptions(
                Strategy: strategy,
                PageFilter: payload.SummaryOptions?.PageFilter,
                IsShallow: payload.SummaryOptions?.ShallowSummary ?? false);

            await summarizationService.GenerateDocumentSummariesAsync(
                summaryDocs, resolvedCollection, summaryOptions);
        }

        await Task.Yield();

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
        var baseUploadFolder = Path.Combine(_tempDir, "uploaded_files", collectionName);
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

    private async Task IngestIntoVectorStoreAsync(
        string collectionName,
        IReadOnlyList<InMemoryIngestorStore.StoredDocument> documents,
        bool isUpdate)
    {
        await chromaStore.EnsureCollectionAsync(collectionName);

        if (isUpdate)
        {
            var docNames = documents.Select(d => d.DocumentName).ToList();
            await chromaStore.DeleteDocumentsAsync(collectionName, docNames);
        }

        var allChunks = new List<VectorDocument>();

        foreach (var doc in documents)
        {
            var uploadPath = doc.DocumentInfo.TryGetValue("upload_path", out var p) ? p?.ToString() : null;
            if (uploadPath is null || !File.Exists(uploadPath))
            {
                logger.LogWarning("Skipping ChromaDB upsert for '{Doc}': file not found at '{Path}'", doc.DocumentName, uploadPath);
                continue;
            }

            string text;
            try
            {
                text = await ExtractTextAsync(uploadPath, doc.DocumentName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not extract text from '{Path}', skipping.", uploadPath);
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogWarning("No text extracted from '{Doc}', skipping.", doc.DocumentName);
                continue;
            }

            var ext = Path.GetExtension(doc.DocumentName).ToLowerInvariant();
            var chunks = ext == ".md"
                ? DocumentChunker.ChunkMarkdown(text, maxTokensPerParagraph: config.ChunkSize, overlapTokens: config.ChunkOverlap)
                : DocumentChunker.ChunkText(text, maxTokensPerParagraph: config.ChunkSize, overlapTokens: config.ChunkOverlap);

            var baseMeta = doc.Metadata
                .ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty);
            baseMeta["filename"] = doc.DocumentName;
            baseMeta["collection_name"] = collectionName;

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
            await vectorStore.UpsertAsync(collectionName, allChunks);
            logger.LogInformation(
                "Upserted {ChunkCount} chunk(s) from {DocCount} document(s) into ChromaDB collection '{Collection}'",
                allChunks.Count,
                documents.Count,
                collectionName);
        }
    }

    // ── Text extraction ───────────────────────────────────────────────────────

    private static async Task<string> ExtractTextAsync(string path, string filename)
    {
        return Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".pdf" => ExtractPdfText(path),
            ".docx" => ExtractDocxText(path),
            ".pptx" => ExtractPptxText(path),
            ".html" or ".htm" => ExtractHtmlText(await File.ReadAllTextAsync(path)),
            _ => await File.ReadAllTextAsync(path)
        };
    }

    private static string ExtractPdfText(string path)
    {
        using var document = PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            sb.AppendLine(ContentOrderTextExtractor.GetText(page));
        }

        return sb.ToString();
    }

    private static string ExtractDocxText(string path)
    {
        using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, false);
        return doc.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty;
    }

    private static string ExtractPptxText(string path)
    {
        using var prs = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(path, false);
        var sb = new StringBuilder();
        foreach (var slidePart in prs.PresentationPart?.SlideParts ?? [])
        {
            var texts = slidePart.Slide
                .Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                .Select(t => t.Text);
            sb.AppendLine(string.Join(" ", texts));
        }

        return sb.ToString();
    }

    private static string ExtractHtmlText(string html) =>
        HtmlTagRegex.Replace(html, " ").Trim();

    private string ResolveCollectionName(string? collectionName)
    {
        return string.IsNullOrWhiteSpace(collectionName)
            ? DefaultCollectionName
            : collectionName;
    }

    // ── Summarization helpers ─────────────────────────────────────────────────

    // ORIG: nvidia_rag/utils/summarization.py::_prepare_single_document
    // Applies page filter for PDFs/PPTX; falls back to full text for other formats.
    private async Task<IReadOnlyList<DocumentContent>> PrepareSummaryDocumentsAsync(
        IReadOnlyList<InMemoryIngestorStore.StoredDocument> documents,
        object? pageFilter)
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
                text = await ExtractTextAsync(path, doc.DocumentName);
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                result.Add(new DocumentContent(doc.DocumentName, text));
            }
        }

        return result;
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
