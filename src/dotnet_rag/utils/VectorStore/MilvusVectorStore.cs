using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Options;
using Microsoft.Extensions.Logging;

namespace DotnetRag.Shared.VectorStore;

// ORIG: nvidia_rag/utils/vdb_utils.py — Milvus backend via langchain_milvus.
// Uses the Milvus v2 REST API (port 9091) so no extra NuGet package is required.
//
// Schema per collection:
//   id        VarChar  primary key
//   text      VarChar  document content
//   embedding FloatVector
//   metadata  JSON     all per-document metadata (filename, etc.)
public sealed class MilvusVectorStore : IVectorStore
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HttpClient _http;
    private readonly IEmbeddingService _embedder;
    private readonly VectorStoreOptions _opts;
    private readonly int _embeddingDim;
    private readonly ILogger<MilvusVectorStore> _logger;

    // Milvus REST API base — port 9091 (REST gateway, confirmed in healthz check)
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

        var rows = new List<object>(documents.Count);
        foreach (var doc in documents)
        {
            var vec = doc.Embedding?.Count > 0
                ? doc.Embedding
                : await _embedder.EmbedAsync(doc.Text, cancellationToken);

            rows.Add(new
            {
                id = doc.Id,
                text = doc.Text,
                embedding = vec,
                metadata = (object)(doc.Metadata ?? new Dictionary<string, string>())
            });
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

        var payloadDict = new Dictionary<string, object?>
        {
            ["collectionName"] = collectionName,
            ["data"] = new[] { embedding },
            ["annsField"] = "embedding",
            ["limit"] = topK,
            ["outputFields"] = new[] { "id", "text", "metadata" }
        };

        if (!string.IsNullOrWhiteSpace(filterExpr))
            payloadDict["filter"] = filterExpr;

        var response = await PostAsync("entities/search", payloadDict, cancellationToken);

        if (!response.IsSuccessStatusCode)
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
            var id = item?["id"]?.GetValue<string>() ?? string.Empty;
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
            var response = await _http.GetAsync(
                $"{BaseUrl}/collections/describe?collectionName={Uri.EscapeDataString(collectionName)}",
                cancellationToken);

            if (!response.IsSuccessStatusCode) return string.Empty;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var root = JsonNode.Parse(body);
            var fields = root?["data"]?["schema"]?["fields"]?.AsArray();
            if (fields is null) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine($"Collection '{collectionName}' schema fields:");
            foreach (var field in fields)
            {
                var name = field?["fieldName"]?.GetValue<string>();
                var dtype = field?["dataType"]?.GetValue<string>();
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
            var response = await _http.GetAsync(
                $"{BaseUrl}/collections/describe?collectionName={Uri.EscapeDataString(collectionName)}",
                cancellationToken);
            return response.IsSuccessStatusCode;
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
        await PostAsync("collections/drop", payload, cancellationToken);
    }

    public async Task DeleteDocumentsAsync(
        string collectionName,
        IReadOnlyList<string> documentNames,
        CancellationToken cancellationToken = default)
    {
        foreach (var name in documentNames)
        {
            var escapedName = name.Replace("\"", "\\\"");
            var payload = new
            {
                collectionName,
                filter = $"metadata[\"filename\"] == \"{escapedName}\""
            };
            await PostAsync("entities/delete", payload, cancellationToken);
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
                fields = new object[]
                {
                    new { fieldName = "id", dataType = "VarChar", isPrimary = true,
                          elementTypeParams = new { max_length = "512" } },
                    new { fieldName = "text", dataType = "VarChar",
                          elementTypeParams = new { max_length = "65535" } },
                    new { fieldName = "embedding", dataType = "FloatVector",
                          elementTypeParams = new { dim = _embeddingDim.ToString() } },
                    new { fieldName = "metadata", dataType = "JSON" }
                }
            },
            indexParams = new[]
            {
                new { fieldName = "embedding", indexName = "idx_embedding",
                      indexType = "AUTOINDEX", metricType = "COSINE" }
            }
        };

        var response = await PostAsync("collections/create", payload, cancellationToken);
        await EnsureSuccessAsync(response, "create-collection", cancellationToken);

        _logger.LogInformation(
            "Created Milvus collection '{Collection}' (dim={Dim})", collectionName, _embeddingDim);
    }

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
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Milvus {Operation} failed ({Status}): {Body}",
                operation, (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }
    }
}
