using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Options;
using Microsoft.Extensions.Logging;

namespace DotnetRag.Shared.VectorStore;

// ORIG: nvidia_rag/utils/vdb_utils.py — Milvus backend via langchain_milvus.
// Uses the Milvus v2 REST API (port 19530 in standalone Milvus) so no extra NuGet package is required.
//
// Schema per collection:
//   id        VarChar  primary key
//   text      VarChar  document content
//   vector    FloatVector
//   metadata  JSON     all per-document metadata (filename, etc.)
public sealed class MilvusVectorStore : IVectorStore, IVectorStoreManagement, IVectorDocumentLookup
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HttpClient _http;
    private readonly IEmbeddingService _embedder;
    private readonly VectorStoreOptions _opts;
    private readonly int _embeddingDim;
    private readonly ILogger<MilvusVectorStore> _logger;

    private string BaseUrl => $"{_opts.Endpoint.TrimEnd('/')}/v2/vectordb";

    public MilvusVectorStore(
        IHttpClientFactory httpClientFactory,
        IEmbeddingService embedder,
        VectorStoreOptions opts,
        int embeddingDim,
        string? token,
        ILogger<MilvusVectorStore> logger)
    {
        _http = httpClientFactory.CreateClient("milvus");
        if (!string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _embedder = embedder;
        _opts = opts;
        _embeddingDim = embeddingDim;
        _logger = logger;
    }

    // ── IVectorStore ──────────────────────────────────────────────────────────

    public async Task UpsertAsync(
        string collectionName,
        IReadOnlyList<VectorDocument> documents,
        CancellationToken cancellationToken = default)
    {
        if (documents.Count == 0) return;

        await EnsureCollectionAsync(collectionName, cancellationToken);
        var schema = await GetSchemaFieldsAsync(collectionName, cancellationToken);
        var vectorField = GetVectorField(schema);

        var rows = new List<Dictionary<string, object?>>(documents.Count);
        foreach (var doc in documents)
        {
            var vec = doc.Embedding?.Count > 0
                ? doc.Embedding
                : await _embedder.EmbedAsync(doc.Text, cancellationToken);
            var metadata = doc.Metadata ?? new Dictionary<string, object?>();
            var filename = GetMetadataString(metadata, "filename") ?? doc.Id;

            var row = new Dictionary<string, object?>();
            if (schema.Contains("pk"))
            {
                row["pk"] = StablePositiveInt64(doc.Id);
            }
            if (schema.Contains("id"))
            {
                row["id"] = doc.Id;
            }
            if (schema.Contains("text"))
            {
                row["text"] = doc.Text;
            }
            row[vectorField] = vec;
            if (schema.Contains("source"))
            {
                row["source"] = new Dictionary<string, object?>
                {
                    ["source_id"] = filename,
                    ["source_name"] = filename,
                    ["source_location"] = GetMetadataString(metadata, "source_location") ?? string.Empty
                };
            }
            if (schema.Contains("content_metadata"))
            {
                row["content_metadata"] = new Dictionary<string, object?>
                {
                    ["type"] = GetMetadataString(metadata, "type") ?? "text",
                    ["page_number"] = GetMetadataNumber(metadata, "page_number") ?? 0
                };
            }
            if (schema.Contains("metadata"))
            {
                row["metadata"] = metadata;
            }
            rows.Add(row);
        }

        var payload = new { collectionName, data = rows };
        var response = await PostAsync("entities/upsert", payload, cancellationToken);
        await EnsureSuccessAsync(response, "upsert", cancellationToken);

        _logger.LogInformation(
            "Milvus upserted {Count} document(s) into '{Collection}'",
            documents.Count, collectionName);
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collectionName,
        string query,
        int topK,
        CancellationToken cancellationToken = default)
        => await SearchAsync(collectionName, query, topK, null, cancellationToken);

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collectionName,
        string query,
        int topK,
        string? filterExpr,
        CancellationToken cancellationToken = default)
    {
        var embedding = await _embedder.EmbedAsync(query, cancellationToken);
        var schema = await GetSchemaFieldsAsync(collectionName, cancellationToken);
        var vectorField = GetVectorField(schema);
        var outputFields = PreferredOutputFields(schema);

        var payloadDict = new Dictionary<string, object?>
        {
            ["collectionName"] = collectionName,
            ["data"] = new[] { embedding },
            ["annsField"] = vectorField,
            ["limit"] = topK,
            ["outputFields"] = outputFields
        };

        if (!string.IsNullOrWhiteSpace(filterExpr))
            payloadDict["filter"] = filterExpr;

        var response = await PostAsync("entities/search", payloadDict, cancellationToken);

        if (!await IsSuccessAsync(response, cancellationToken))
        {
            _logger.LogWarning(
                "Milvus search failed ({Status}) for collection '{Collection}'; returning empty.",
                (int)response.StatusCode, collectionName);
            return [];
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var root = JsonNode.Parse(body);
        var data = root?["data"]?.AsArray();
        if (data is null) return [];

        var results = new List<VectorSearchResult>(data.Count);
        foreach (var item in data)
        {
            var id = item?["id"]?.GetValue<string>()
                ?? item?["source"]?["source_id"]?.GetValue<string>()
                ?? string.Empty;
            var text = item?["text"]?.GetValue<string>() ?? string.Empty;
            var distance = item?["distance"]?.GetValue<double>() ?? 0.0;
            // Milvus COSINE returns similarity directly (1 = identical)
            var score = distance;

            var meta = new Dictionary<string, string>();
            var metaNode = item?["metadata"];
            if (metaNode is JsonObject metaObj)
            {
                foreach (var kv in metaObj)
                    meta[kv.Key] = kv.Value?.GetValue<string>() ?? string.Empty;
            }
            if (item?["source"] is JsonObject sourceObj)
            {
                foreach (var kv in sourceObj)
                    meta[$"source.{kv.Key}"] = kv.Value?.ToJsonString() ?? string.Empty;
            }
            if (item?["content_metadata"] is JsonObject contentMetaObj)
            {
                foreach (var kv in contentMetaObj)
                    meta[$"content_metadata.{kv.Key}"] = kv.Value?.ToJsonString() ?? string.Empty;
            }

            results.Add(new VectorSearchResult(id, text, score, meta));
        }

        return results;
    }

    public async Task<string> GetSchemaDescriptionAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await PostAsync(
                "collections/describe",
                new { collectionName },
                cancellationToken);

            if (!await IsSuccessAsync(response, cancellationToken)) return string.Empty;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var root = JsonNode.Parse(body);
            var fields = root?["data"]?["fields"]?.AsArray()
                ?? root?["data"]?["schema"]?["fields"]?.AsArray();
            if (fields is null) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine($"Collection '{collectionName}' schema fields:");
            foreach (var field in fields)
            {
                var name = field?["fieldName"]?.GetValue<string>()
                    ?? field?["name"]?.GetValue<string>();
                var dtype = field?["dataType"]?.GetValue<string>()
                    ?? field?["type"]?.GetValue<string>();
                if (name is not null)
                    sb.AppendLine($"  - {name} ({dtype})");
            }

            sb.AppendLine("The 'metadata' field is a JSON object — filter with: metadata[\"key\"] == \"value\", metadata[\"num\"] > 0, etc.");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve Milvus schema for '{Collection}'", collectionName);
            return string.Empty;
        }
    }

    // ── Collection helpers ────────────────────────────────────────────────────

    public async Task EnsureCollectionAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        if (await CollectionExistsAsync(collectionName, cancellationToken)) return;
        await CreateCollectionAsync(collectionName, cancellationToken);
    }

    public async Task<bool> CollectionExistsAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await PostAsync(
                "collections/describe",
                new { collectionName },
                cancellationToken);
            return await IsSuccessAsync(response, cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    public async Task DeleteCollectionAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        var payload = new { collectionName };
        var response = await PostAsync("collections/drop", payload, cancellationToken);
        await EnsureSuccessAsync(response, "drop-collection", cancellationToken);
    }

    public async Task<bool> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await PostAsync(
                "collections/list",
                new { },
                cancellationToken);
            return await IsSuccessAsync(response, cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    public async Task DeleteDocumentsAsync(
        string collectionName,
        IReadOnlyList<string> documentNames,
        CancellationToken cancellationToken = default)
    {
        foreach (var name in documentNames)
        {
            var escapedName = name.Replace("\"", "\\\"");
            var schema = await GetSchemaFieldsAsync(collectionName, cancellationToken);
            var filter = schema.Contains("metadata")
                ? $"metadata[\"filename\"] == \"{escapedName}\""
                : schema.Contains("source")
                    ? $"source[\"source_id\"] == \"{escapedName}\""
                    : $"id like \"{escapedName}%\"";
            var payload = new
            {
                collectionName,
                filter
            };
            var response = await PostAsync("entities/delete", payload, cancellationToken);
            await EnsureSuccessAsync(response, "delete-entities", cancellationToken);
        }
    }

    public async Task<IReadOnlyList<string>> ListDocumentNamesAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        if (!await CollectionExistsAsync(collectionName, cancellationToken))
        {
            return [];
        }

        var schema = await GetSchemaFieldsAsync(collectionName, cancellationToken);
        var outputFields = schema.Contains("metadata")
            ? new[] { "metadata" }
            : schema.Contains("source")
                ? new[] { "source" }
                : schema.Contains("id")
                    ? new[] { "id" }
                    : Array.Empty<string>();

        var payload = new
        {
            collectionName,
            outputFields,
            limit = 16_384
        };

        var response = await PostAsync("entities/query", payload, cancellationToken);
        if (!await IsSuccessAsync(response, cancellationToken))
        {
            _logger.LogWarning(
                "Milvus document list failed ({Status}) for collection '{Collection}'",
                (int)response.StatusCode,
                collectionName);
            return [];
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var root = JsonNode.Parse(body);
        var data = root?["data"]?.AsArray();
        if (data is null)
        {
            return [];
        }

        return data
            .Select(item => item?["metadata"]?["filename"]?.GetValue<string>()
                ?? item?["source"]?["source_id"]?.GetValue<string>()
                ?? item?["id"]?.GetValue<string>()?.Split("__chunk_")[0])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string?> GetDocumentTextByIdAsync(
        string collectionName,
        string id,
        CancellationToken cancellationToken = default)
    {
        if (!await CollectionExistsAsync(collectionName, cancellationToken))
        {
            return null;
        }

        var escapedId = id.Replace("\"", "\\\"");
        var schema = await GetSchemaFieldsAsync(collectionName, cancellationToken);
        var filter = schema.Contains("id")
            ? $"id == \"{escapedId}\""
            : schema.Contains("source")
                ? $"source[\"source_id\"] == \"{escapedId}\""
                : $"id == \"{escapedId}\"";
        var payload = new
        {
            collectionName,
            filter,
            outputFields = new[] { "text" },
            limit = 1
        };

        try
        {
            var response = await PostAsync("entities/query", payload, cancellationToken);
            if (!await IsSuccessAsync(response, cancellationToken))
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var root = JsonNode.Parse(body);
            var data = root?["data"]?.AsArray();
            return data is { Count: > 0 }
                ? data[0]?["text"]?.GetValue<string>()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task CompactCollectionAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        var payload = new { collectionName };
        var response = await PostAsync("collections/compact", payload, cancellationToken);
        if (!await IsSuccessAsync(response, cancellationToken))
        {
            _logger.LogDebug(
                "Milvus compact is unavailable or failed for collection '{Collection}' ({Status}).",
                collectionName,
                (int)response.StatusCode);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task CreateCollectionAsync(
        string collectionName,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            collectionName,
            schema = new
            {
                enableDynamicField = true,
                fields = new object[]
                {
                    new { fieldName = "pk", dataType = "Int64", isPrimary = true, autoId = true },
                    new { fieldName = "id", dataType = "VarChar",
                          elementTypeParams = new { max_length = "512" } },
                    new { fieldName = "text", dataType = "VarChar",
                          elementTypeParams = new { max_length = "65535" } },
                    new { fieldName = "vector", dataType = "FloatVector",
                          elementTypeParams = new { dim = _embeddingDim.ToString() } },
                    new { fieldName = "source", dataType = "JSON" },
                    new { fieldName = "content_metadata", dataType = "JSON" },
                    new { fieldName = "metadata", dataType = "JSON" }
                }
            },
            indexParams = new[]
            {
                new { fieldName = "vector", indexName = "idx_vector",
                      indexType = "AUTOINDEX", metricType = "COSINE" }
            }
        };

        var response = await PostAsync("collections/create", payload, cancellationToken);
        await EnsureSuccessAsync(response, "create-collection", cancellationToken);

        _logger.LogInformation(
            "Created Milvus collection '{Collection}' (dim={Dim})", collectionName, _embeddingDim);
    }

    private static string? GetMetadataString(
        IReadOnlyDictionary<string, object?> metadata,
        string key)
    {
        return metadata.TryGetValue(key, out var value) && value is not null
            ? value.ToString()
            : null;
    }

    private static double? GetMetadataNumber(
        IReadOnlyDictionary<string, object?> metadata,
        string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int item => item,
            long item => item,
            float item => item,
            double item => item,
            decimal item => (double)item,
            string item when double.TryParse(item, out var parsed) => parsed,
            _ => null
        };
    }

    private static long StablePositiveInt64(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= prime;
        }

        return (long)(hash & 0x7FFF_FFFF_FFFF_FFFFUL);
    }

    private async Task<HashSet<string>> GetSchemaFieldsAsync(
        string collectionName,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await PostAsync(
                "collections/describe",
                new { collectionName },
                cancellationToken);

            if (!await IsSuccessAsync(response, cancellationToken))
            {
                return DefaultSchemaFields();
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var root = JsonNode.Parse(body);
            var fields = root?["data"]?["fields"]?.AsArray()
                ?? root?["data"]?["schema"]?["fields"]?.AsArray();
            if (fields is null)
            {
                return DefaultSchemaFields();
            }

            var names = fields
                .Select(field => field?["fieldName"]?.GetValue<string>()
                    ?? field?["name"]?.GetValue<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return names.Count > 0 ? names : DefaultSchemaFields();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to inspect Milvus schema for '{Collection}'", collectionName);
        }

        return DefaultSchemaFields();
    }

    private static HashSet<string> DefaultSchemaFields() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            "pk",
            "id",
            "text",
            "vector",
            "source",
            "content_metadata",
            "metadata"
        };

    private static string GetVectorField(IReadOnlySet<string> schema)
    {
        if (schema.Contains("vector"))
        {
            return "vector";
        }

        // Compatibility for collections created before .NET matched Python's Milvus schema.
        if (schema.Contains("embedding"))
        {
            return "embedding";
        }

        return "vector";
    }

    private static string[] PreferredOutputFields(IReadOnlySet<string> schema) =>
        new[] { "id", "text", "metadata", "source", "content_metadata" }
            .Where(schema.Contains)
            .ToArray();

    private async Task<HttpResponseMessage> PostAsync(
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _http.PostAsync($"{BaseUrl}/{path}", content, cancellationToken);
    }

    private async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (!await IsSuccessAsync(response, cancellationToken))
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Milvus {Operation} failed ({Status}): {Body}",
                operation, (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode();
            throw new HttpRequestException($"Milvus {operation} failed: {body}");
        }
    }

    private static async Task<bool> IsSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) return false;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body)) return true;

        try
        {
            var root = JsonNode.Parse(body);
            return root?["code"]?.GetValue<int>() == 0;
        }
        catch (JsonException)
        {
            return true;
        }
    }
}
