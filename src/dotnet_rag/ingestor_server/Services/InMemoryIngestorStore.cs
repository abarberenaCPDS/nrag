using System.Collections.Concurrent;
using System.Text.Json;
using DotnetRag.Ingestor.Models;

namespace DotnetRag.Ingestor.Services;

public sealed class InMemoryIngestorStore(IIngestorCatalogStore catalogStore)
{
    private readonly ConcurrentDictionary<string, IngestorCatalogEntry> _collections =
        new(
            catalogStore.Load()
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .Select(entry => new KeyValuePair<string, IngestorCatalogEntry>(entry.Name, entry)),
            StringComparer.OrdinalIgnoreCase);

    public InMemoryIngestorStore()
        : this(CreateCatalogStoreForEnvironment())
    {
    }

    public bool CollectionExists(string collectionName) => _collections.ContainsKey(collectionName);

    public bool CreateCollection(CreateCollectionRequest request)
    {
        var entry = new IngestorCatalogEntry
        {
            Name = request.CollectionName,
            Description = request.Description,
            Tags = [.. request.Tags],
            Owner = request.Owner,
            CreatedBy = request.CreatedBy,
            BusinessDomain = request.BusinessDomain,
            Status = request.Status,
            MetadataSchema = MetadataSchemaValidator.NormalizeSchema(request.MetadataSchema)
                .Select(field => new Dictionary<string, object?>(field))
                .ToList()
        };

        var created = _collections.TryAdd(request.CollectionName, entry);
        if (created)
        {
            PersistCatalog();
        }

        return created;
    }

    public IReadOnlyList<UploadedCollection> GetCollections()
    {
        return _collections.Values
            .Select(collection => new UploadedCollection
            {
                CollectionName = collection.Name,
                NumEntities = collection.Documents.Count,
                MetadataSchema = collection.MetadataSchema
                    .Where(field => !field.TryGetValue("user_defined", out var userDefined)
                        || userDefined is true
                        || string.Equals(userDefined?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
                    .Select(field => field
                        .Where(kv => kv.Key is not ("user_defined" or "support_dynamic_filtering"))
                        .ToDictionary(kv => kv.Key, kv => kv.Value))
                    .ToList(),
                CollectionInfo = new Dictionary<string, object?>
                {
                    ["description"] = collection.Description,
                    ["tags"] = collection.Tags,
                    ["owner"] = collection.Owner,
                    ["created_by"] = collection.CreatedBy,
                    ["business_domain"] = collection.BusinessDomain,
                    ["status"] = collection.Status
                }
                .Concat(BuildCollectionMetrics(collection.Documents))
                .ToDictionary(kv => kv.Key, kv => kv.Value)
            })
            .OrderBy(item => item.CollectionName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool DeleteCollection(string collectionName)
    {
        var deleted = _collections.TryRemove(collectionName, out _);
        if (deleted)
        {
            PersistCatalog();
        }

        return deleted;
    }

    public bool UpdateCollectionMetadata(
        string collectionName,
        UpdateCollectionMetadataRequest request)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
        {
            return false;
        }

        lock (collection.SyncRoot)
        {
            if (request.Description is not null)
            {
                collection.Description = request.Description;
            }

            if (request.Tags is not null)
            {
                collection.Tags = [.. request.Tags];
            }

            if (request.Owner is not null)
            {
                collection.Owner = request.Owner;
            }

            if (request.BusinessDomain is not null)
            {
                collection.BusinessDomain = request.BusinessDomain;
            }

            if (request.Status is not null)
            {
                collection.Status = request.Status;
            }
        }

        PersistCatalog();
        return true;
    }

    public IReadOnlyList<UploadedDocument> GetDocuments(
        string collectionName,
        int maxResults)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
        {
            return [];
        }

        lock (collection.SyncRoot)
        {
            return collection.Documents
                .Take(Math.Max(0, maxResults))
                .Select(document => document.ToUploadedDocument())
                .ToList();
        }
    }

    public int GetDocumentCount(string collectionName)
    {
        return _collections.TryGetValue(collectionName, out var collection)
            ? collection.Documents.Count
            : 0;
    }

    public HashSet<string> GetDocumentNames(string collectionName)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
        {
            return [];
        }

        lock (collection.SyncRoot)
        {
            return collection.Documents
                .Select(item => item.DocumentName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<StoredDocument> GetStoredDocuments(string collectionName)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
        {
            return [];
        }

        lock (collection.SyncRoot)
        {
            return collection.Documents
                .Select(document => new StoredDocument
                {
                    DocumentName = document.DocumentName,
                    Metadata = new Dictionary<string, object?>(document.Metadata),
                    DocumentInfo = new Dictionary<string, object?>(document.DocumentInfo)
                })
                .ToList();
        }
    }

    public void UpsertDocuments(
        string collectionName,
        IEnumerable<StoredDocument> documents,
        bool replaceExisting)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
        {
            return;
        }

        lock (collection.SyncRoot)
        {
            foreach (var document in documents)
            {
                var existing = collection.Documents.FindIndex(item =>
                    string.Equals(
                        item.DocumentName,
                        document.DocumentName,
                        StringComparison.OrdinalIgnoreCase));

                if (existing >= 0)
                {
                    if (!replaceExisting)
                    {
                        continue;
                    }

                    collection.Documents.RemoveAt(existing);
                }

                collection.Documents.Add(document);
            }
        }

        PersistCatalog();
    }

    public List<string> DeleteDocuments(string collectionName, IEnumerable<string> documentNames)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
        {
            return [];
        }

        var deleted = new List<string>();
        var targets = documentNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (collection.SyncRoot)
        {
            if (targets.Count == 0)
            {
                deleted.AddRange(collection.Documents.Select(item => item.DocumentName));
                collection.Documents.Clear();
                PersistCatalog();
                return deleted;
            }

            collection.Documents.RemoveAll(document =>
            {
                if (!targets.Contains(document.DocumentName))
                {
                    return false;
                }

                deleted.Add(document.DocumentName);
                return true;
            });
        }

        if (deleted.Count > 0)
        {
            PersistCatalog();
        }

        return deleted;
    }

    // Validates document metadata against the collection's registered schema.
    // Returns a list of human-readable error messages; empty list means valid.
    public List<string> ValidateDocumentMetadata(
        string collectionName,
        string documentName,
        IReadOnlyDictionary<string, object?> metadata)
    {
        return ValidateAndNormalizeDocumentMetadata(collectionName, documentName, metadata)
            .Errors;
    }

    public MetadataValidationResult ValidateAndNormalizeDocumentMetadata(
        string collectionName,
        string documentName,
        IReadOnlyDictionary<string, object?> metadata)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            return new MetadataValidationResult(
                IsValid: true,
                Errors: [],
                NormalizedMetadata: new Dictionary<string, object?>(metadata));

        var schema = collection.MetadataSchema;
        if (schema.Count == 0)
        {
            return new MetadataValidationResult(
                IsValid: true,
                Errors: [],
                NormalizedMetadata: new Dictionary<string, object?>(metadata));
        }

        var validation = MetadataSchemaValidator.ValidateAndNormalize(schema, metadata);
        return validation with
        {
            Errors = validation.Errors
                .Select(error => $"'{documentName}': {error}")
                .ToList()
        };
    }

    public bool UpdateDocumentMetadata(
        string collectionName,
        string documentName,
        UpdateDocumentMetadataRequest request)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
        {
            return false;
        }

        lock (collection.SyncRoot)
        {
            var document = collection.Documents.FirstOrDefault(item =>
                string.Equals(item.DocumentName, documentName, StringComparison.OrdinalIgnoreCase));
            if (document is null)
            {
                return false;
            }

            if (request.Description is not null)
            {
                document.DocumentInfo["description"] = request.Description;
            }

            if (request.Tags is not null)
            {
                document.DocumentInfo["tags"] = request.Tags;
            }
        }

        PersistCatalog();
        return true;
    }

    private void PersistCatalog()
    {
        catalogStore.Save(_collections.Values
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static IIngestorCatalogStore CreateCatalogStoreForEnvironment() =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APP_INGESTOR_CATALOG_PATH"))
            ? new DisabledIngestorCatalogStore()
            : new FileBackedIngestorCatalogStore();

    private static Dictionary<string, object?> BuildCollectionMetrics(IReadOnlyList<StoredDocument> documents)
    {
        var docTypeCounts = documents
            .Select(document => document.DocumentInfo.TryGetValue("document_type", out var type)
                ? type?.ToString() ?? string.Empty
                : string.Empty)
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .GroupBy(type => type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (object?)group.Count(), StringComparer.OrdinalIgnoreCase);

        long SumLong(string key) => documents.Sum(document =>
        {
            if (!document.DocumentInfo.TryGetValue(key, out var value) || value is null)
            {
                return 0L;
            }

            return value switch
            {
                long l => l,
                int i => i,
                _ => long.TryParse(value.ToString(), out var parsed) ? parsed : 0L
            };
        });

        var lastIndexed = documents
            .Select(document => document.DocumentInfo.TryGetValue("last_indexed", out var value)
                ? value?.ToString()
                : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .OrderByDescending(value => value, StringComparer.Ordinal)
            .FirstOrDefault();

        return new Dictionary<string, object?>
        {
            ["number_of_files"] = documents.Count,
            ["doc_type_counts"] = docTypeCounts,
            ["total_elements"] = SumLong("total_elements"),
            ["raw_text_elements_size"] = SumLong("raw_text_elements_size"),
            ["has_tables"] = documents.Any(document => IsTrue(document, "has_tables")),
            ["has_charts"] = documents.Any(document => IsTrue(document, "has_charts")),
            ["has_images"] = documents.Any(document => IsTrue(document, "has_images")),
            ["last_indexed"] = lastIndexed,
            ["ingestion_status"] = documents.Any(document => string.Equals(
                document.DocumentInfo.GetValueOrDefault("ingestion_status")?.ToString(),
                "failed",
                StringComparison.OrdinalIgnoreCase))
                ? "failed"
                : documents.Count > 0 ? "completed" : "not_started"
        };
    }

    private static bool IsTrue(StoredDocument document, string key) =>
        document.DocumentInfo.TryGetValue(key, out var value)
        && value is not null
        && bool.TryParse(value.ToString(), out var parsed)
        && parsed;

    public sealed class StoredDocument
    {
        public required string DocumentName { get; init; }
        public Dictionary<string, object?> Metadata { get; init; } = [];
        public Dictionary<string, object?> DocumentInfo { get; init; } = [];

        public UploadedDocument ToUploadedDocument()
        {
            return new UploadedDocument
            {
                DocumentId = GetString(DocumentInfo, "document_id") ?? DocumentName,
                DocumentName = DocumentName,
                SizeBytes = GetLong(DocumentInfo, "size_bytes")
                    ?? GetLong(DocumentInfo, "file_size")
                    ?? 0,
                Metadata = new Dictionary<string, object?>(Metadata),
                DocumentInfo = new Dictionary<string, object?>(DocumentInfo)
            };
        }

        private static string? GetString(
            IReadOnlyDictionary<string, object?> values,
            string key)
        {
            return values.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value?.ToString())
                ? value.ToString()
                : null;
        }

        private static long? GetLong(
            IReadOnlyDictionary<string, object?> values,
            string key)
        {
            if (!values.TryGetValue(key, out var value) || value is null)
            {
                return null;
            }

            return value switch
            {
                long l => l,
                int i => i,
                JsonElement element when element.ValueKind == JsonValueKind.Number
                    && element.TryGetInt64(out var parsed) => parsed,
                _ when long.TryParse(value.ToString(), out var parsed) => parsed,
                _ => null
            };
        }
    }

}
