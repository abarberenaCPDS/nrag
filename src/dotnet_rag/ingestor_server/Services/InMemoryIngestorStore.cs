using System.Collections.Concurrent;
using DotnetRag.Ingestor.Models;

namespace DotnetRag.Ingestor.Services;

public sealed class InMemoryIngestorStore
{
    private readonly ConcurrentDictionary<string, CollectionEntry> _collections =
        new(StringComparer.OrdinalIgnoreCase);

    public bool CollectionExists(string collectionName) => _collections.ContainsKey(collectionName);

    public bool CreateCollection(CreateCollectionRequest request)
    {
        var entry = new CollectionEntry
        {
            Name = request.CollectionName,
            Description = request.Description,
            Tags = [.. request.Tags],
            Owner = request.Owner,
            CreatedBy = request.CreatedBy,
            BusinessDomain = request.BusinessDomain,
            Status = request.Status,
            MetadataSchema = request.MetadataSchema
                .Select(field => new Dictionary<string, object?>
                {
                    ["name"] = field.Name,
                    ["type"] = field.Type,
                    ["description"] = field.Description,
                    ["required"] = field.Required
                })
                .ToList()
        };

        return _collections.TryAdd(request.CollectionName, entry);
    }

    public IReadOnlyList<UploadedCollection> GetCollections()
    {
        return _collections.Values
            .Select(collection => new UploadedCollection
            {
                CollectionName = collection.Name,
                NumEntities = collection.Documents.Count,
                MetadataSchema = [.. collection.MetadataSchema],
                CollectionInfo = new Dictionary<string, object?>
                {
                    ["description"] = collection.Description,
                    ["tags"] = collection.Tags,
                    ["owner"] = collection.Owner,
                    ["created_by"] = collection.CreatedBy,
                    ["business_domain"] = collection.BusinessDomain,
                    ["status"] = collection.Status
                }
            })
            .OrderBy(item => item.CollectionName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool DeleteCollection(string collectionName) =>
        _collections.TryRemove(collectionName, out _);

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

        return deleted;
    }

    // Validates document metadata against the collection's registered schema.
    // Returns a list of human-readable error messages; empty list means valid.
    public List<string> ValidateDocumentMetadata(
        string collectionName,
        string documentName,
        IReadOnlyDictionary<string, object?> metadata)
    {
        if (!_collections.TryGetValue(collectionName, out var collection))
            return [];

        var schema = collection.MetadataSchema;
        if (schema.Count == 0) return [];

        var errors = new List<string>();

        foreach (var field in schema)
        {
            var name = field.TryGetValue("name", out var n) ? n?.ToString() : null;
            if (string.IsNullOrEmpty(name)) continue;

            var required = field.TryGetValue("required", out var r) && r is true or "true";
            var expectedType = field.TryGetValue("type", out var t) ? t?.ToString() ?? "str" : "str";

            if (!metadata.TryGetValue(name, out var value) || value is null)
            {
                if (required)
                    errors.Add($"'{documentName}': required metadata field '{name}' is missing.");
                continue;
            }

            var typeError = ValidateFieldType(name, value, expectedType);
            if (typeError is not null)
                errors.Add($"'{documentName}': {typeError}");
        }

        return errors;
    }

    private static string? ValidateFieldType(string fieldName, object? value, string expectedType)
    {
        if (value is null) return null;

        return expectedType.ToLowerInvariant() switch
        {
            "int" or "integer" => value is not (int or long or short or byte)
                && !long.TryParse(value.ToString(), out _)
                    ? $"field '{fieldName}' expected type int, got '{value.GetType().Name}'."
                    : null,
            "float" or "double" or "number" => value is not (float or double or decimal)
                && !double.TryParse(value.ToString(), out _)
                    ? $"field '{fieldName}' expected type float, got '{value.GetType().Name}'."
                    : null,
            "bool" or "boolean" => value is not bool
                && !bool.TryParse(value.ToString(), out _)
                    ? $"field '{fieldName}' expected type bool, got '{value.GetType().Name}'."
                    : null,
            _ => null // "str" and unknown types: accept any value
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

        return true;
    }

    public sealed class StoredDocument
    {
        public required string DocumentName { get; init; }
        public Dictionary<string, object?> Metadata { get; init; } = [];
        public Dictionary<string, object?> DocumentInfo { get; init; } = [];

        public UploadedDocument ToUploadedDocument()
        {
            return new UploadedDocument
            {
                DocumentName = DocumentName,
                Metadata = new Dictionary<string, object?>(Metadata),
                DocumentInfo = new Dictionary<string, object?>(DocumentInfo)
            };
        }
    }

    private sealed class CollectionEntry
    {
        public required string Name { get; init; }
        public string Description { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = [];
        public string Owner { get; set; } = string.Empty;
        public string CreatedBy { get; set; } = string.Empty;
        public string BusinessDomain { get; set; } = string.Empty;
        public string Status { get; set; } = "Active";
        public List<Dictionary<string, object?>> MetadataSchema { get; set; } = [];
        public List<StoredDocument> Documents { get; } = [];
        public object SyncRoot { get; } = new();
    }
}
