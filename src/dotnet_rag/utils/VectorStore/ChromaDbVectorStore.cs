using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Options;
using Microsoft.Extensions.Logging;

namespace DotnetRag.Shared.VectorStore;

/// <summary>
/// ChromaDB REST API client implementing IVectorStore.
/// Talks to the Chroma HTTP API (v1) at the configured endpoint.
/// ORIG_VECTORSTORE: milvus / elasticsearch / lancedb (langchain backends)
/// </summary>
public sealed class ChromaDbVectorStore : IVectorStore
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly HttpClient _http;
    private readonly IEmbeddingService _embedder;
    private readonly VectorStoreOptions _opts;
    private readonly ILogger<ChromaDbVectorStore> _logger;

    private string CollectionsUrl =>
        $"{_opts.Endpoint}/api/v2/tenants/{_opts.Tenant}/databases/{_opts.Database}/collections";

    public ChromaDbVectorStore(
        IHttpClientFactory httpClientFactory,
        IEmbeddingService embedder,
        VectorStoreOptions opts,
        ILogger<ChromaDbVectorStore> logger)
    {
        _http = httpClientFactory.CreateClient("chroma");
        _embedder = embedder;
        _opts = opts;
        _logger = logger;
    }

    // ── Collection helpers ────────────────────────────────────────────────────

    /// <summary>Ensures a collection exists; creates it if absent.</summary>
    public async Task EnsureCollectionAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        var id = await TryGetCollectionIdAsync(collectionName, cancellationToken);
        if (id is null)
        {
            await CreateCollectionAsync(collectionName, cancellationToken);
        }
    }

    public async Task<bool> CollectionExistsAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        var id = await TryGetCollectionIdAsync(collectionName, cancellationToken);
        return id is not null;
    }

    public async Task<IReadOnlyList<string>> ListCollectionsAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync($"{CollectionsUrl}", cancellationToken);
        response.EnsureSuccessStatusCode();
        var root = await response.Content.ReadFromJsonAsync<JsonArray>(cancellationToken: cancellationToken);
        return root?
            .Select(n => n?["name"]?.GetValue<string>())
            .Where(n => n is not null)
            .Cast<string>()
            .ToList()
            ?? [];
    }

    public async Task DeleteCollectionAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync(
            $"{CollectionsUrl}/{Uri.EscapeDataString(collectionName)}",
            cancellationToken);

        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task<IReadOnlyList<string>> ListDocumentNamesAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        var colId = await RequireCollectionIdAsync(collectionName, cancellationToken);
        var url = $"{CollectionsUrl}/{colId}/get";
        var payload = new { include = new[] { "metadatas" } };
        var response = await PostJsonAsync(url, payload, cancellationToken);
        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var metadatas = root?["metadatas"]?.AsArray();
        if (metadatas is null)
        {
            return [];
        }

        return metadatas
            .Select(m => m?["filename"]?.GetValue<string>())
            .Where(n => n is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task DeleteDocumentsAsync(
        string collectionName,
        IReadOnlyList<string> documentNames,
        CancellationToken cancellationToken = default)
    {
        var colId = await TryGetCollectionIdAsync(collectionName, cancellationToken);
        if (colId is null)
        {
            return;
        }

        // Query ids for documents matching the given filenames
        foreach (var docName in documentNames)
        {
            var queryUrl = $"{CollectionsUrl}/{colId}/get";
            var queryPayload = new
            {
                where = new Dictionary<string, object> { ["filename"] = docName },
                include = Array.Empty<string>()
            };

            var queryResponse = await PostJsonAsync(queryUrl, queryPayload, cancellationToken);
            var root = JsonNode.Parse(await queryResponse.Content.ReadAsStringAsync(cancellationToken));
            var ids = root?["ids"]?.AsArray()
                .Select(n => n?.GetValue<string>())
                .Where(n => n is not null)
                .Cast<string>()
                .ToList();

            if (ids is { Count: > 0 })
            {
                var deleteUrl = $"{CollectionsUrl}/{colId}/delete";
                await PostJsonAsync(deleteUrl, new { ids }, cancellationToken);
            }
        }
    }

    // ── IVectorStore ──────────────────────────────────────────────────────────

    public async Task UpsertAsync(
        string collectionName,
        IReadOnlyList<VectorDocument> documents,
        CancellationToken cancellationToken = default)
    {
        if (documents.Count == 0)
        {
            return;
        }

        var colId = await RequireCollectionIdAsync(collectionName, cancellationToken);

        // Embed any documents that don't already carry a vector
        var ids = new List<string>(documents.Count);
        var texts = new List<string>(documents.Count);
        var embeddings = new List<IReadOnlyList<float>>(documents.Count);
        var metadatas = new List<Dictionary<string, string>>(documents.Count);

        foreach (var doc in documents)
        {
            ids.Add(doc.Id);
            texts.Add(doc.Text);

            var vec = doc.Embedding?.Count > 0
                ? doc.Embedding
                : await _embedder.EmbedAsync(doc.Text, cancellationToken);
            embeddings.Add(vec);

            metadatas.Add(doc.Metadata is null
                ? []
                : doc.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value));
        }

        var url = $"{CollectionsUrl}/{colId}/upsert";
        var payload = new
        {
            ids,
            documents = texts,
            embeddings,
            metadatas
        };

        var response = await PostJsonAsync(url, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "ChromaDB upsert failed ({StatusCode}): {Body}",
                (int)response.StatusCode,
                body);
            response.EnsureSuccessStatusCode();
        }

        _logger.LogInformation(
            "Upserted {Count} document(s) into ChromaDB collection '{Collection}'",
            documents.Count,
            collectionName);
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collectionName,
        string query,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var colId = await TryGetCollectionIdAsync(collectionName, cancellationToken);
        if (colId is null)
        {
            _logger.LogWarning(
                "ChromaDB collection '{Collection}' not found for search; returning empty results.",
                collectionName);
            return [];
        }

        var queryEmbedding = await _embedder.EmbedAsync(query, cancellationToken);

        var url = $"{CollectionsUrl}/{colId}/query";
        var payload = new
        {
            query_embeddings = new[] { queryEmbedding },
            n_results = topK,
            include = new[] { "documents", "metadatas", "distances" }
        };

        var response = await PostJsonAsync(url, payload, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var root = JsonNode.Parse(body);

        var ids = root?["ids"]?[0]?.AsArray();
        var docs = root?["documents"]?[0]?.AsArray();
        var distances = root?["distances"]?[0]?.AsArray();
        var metas = root?["metadatas"]?[0]?.AsArray();

        var results = new List<VectorSearchResult>();
        int count = ids?.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            var id = ids![i]?.GetValue<string>() ?? string.Empty;
            var text = docs?[i]?.GetValue<string>() ?? string.Empty;
            // ChromaDB returns L2 distance; convert to similarity score (lower distance = higher score)
            var distance = distances?[i]?.GetValue<double>() ?? 0.0;
            var score = 1.0 / (1.0 + distance);

            var meta = metas?[i]?.AsObject()
                ?.ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? string.Empty)
                ?? new Dictionary<string, string>();

            results.Add(new VectorSearchResult(id, text, score, meta));
        }

        return results;
    }

    // ── Summary retrieval ─────────────────────────────────────────────────────

    /// <summary>
    /// Retrieves the document text for a specific ChromaDB document by id.
    /// Returns null if the collection or document does not exist.
    /// ORIG: nvidia_rag/utils/summarization.py::_store_summary_in_object_store (retrieval side)
    /// </summary>
    public async Task<string?> GetDocumentTextByIdAsync(
        string collectionName,
        string id,
        CancellationToken cancellationToken = default)
    {
        var colId = await TryGetCollectionIdAsync(collectionName, cancellationToken);
        if (colId is null) return null;

       var url = $"{CollectionsUrl}/{colId}/get";
        var payload = new
        {
            ids = new[] { id },
            include = new[] { "documents" }
        };

        try
        {
            var response = await PostJsonAsync(url, payload, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var root = JsonNode.Parse(body);
            var docs = root?["documents"]?.AsArray();
            return docs is { Count: > 0 } ? docs[0]?.GetValue<string>() : null;
        }
        catch
        {
            return null;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<string?> TryGetCollectionIdAsync(
        string collectionName,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _http.GetAsync(
                $"{CollectionsUrl}/{Uri.EscapeDataString(collectionName)}",
                cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            var root = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
            return root?["id"]?.GetValue<string>();
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<string> RequireCollectionIdAsync(
        string collectionName,
        CancellationToken cancellationToken)
    {
        var id = await TryGetCollectionIdAsync(collectionName, cancellationToken);
        if (id is null)
        {
            id = await CreateCollectionAsync(collectionName, cancellationToken);
        }

        return id;
    }

    private async Task<string> CreateCollectionAsync(
        string collectionName,
        CancellationToken cancellationToken)
    {
        var url = $"{CollectionsUrl}";
        var payload = new { name = collectionName };
        var response = await PostJsonAsync(url, payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        var root = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
        var id = root?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("ChromaDB did not return a collection ID.");

        _logger.LogInformation("Created ChromaDB collection '{Collection}' (id={Id})", collectionName, id);
        return id;
    }

    private async Task<HttpResponseMessage> PostJsonAsync(
        string url,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _http.PostAsync(url, content, cancellationToken);
    }
}