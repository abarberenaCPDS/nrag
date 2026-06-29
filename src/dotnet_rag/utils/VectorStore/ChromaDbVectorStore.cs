using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Options;
using Microsoft.Extensions.Logging;

namespace DotnetRag.Shared.VectorStore;

/// <summary>
/// ChromaDB REST API client implementing IVectorStore.
/// Talks to the Chroma HTTP API (v1) at the configured endpoint.
/// ORIG_VECTORSTORE: milvus / elasticsearch / lancedb (langchain backends)
/// </summary>
public sealed class ChromaDbVectorStore : IVectorStore, IVectorStoreManagement, IVectorDocumentLookup, IVectorStoreFilterCapabilities
{
    private const string MetadataSchemaCollection = "metadata_schema";
    private const string DocumentInfoCollection = "document_info";

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
        ILogger<ChromaDbVectorStore> logger,
        string? token = null)
    {
        _http = httpClientFactory.CreateClient("chroma");
        if (!string.IsNullOrWhiteSpace(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

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
        var collections = await ListChromaCollectionsAsync(cancellationToken);
        return collections.Select(collection => collection.Name).ToList();
    }

    async Task<IReadOnlyList<VectorStoreCollectionDetails>> IVectorStoreManagement.ListCollectionsAsync(
        CancellationToken cancellationToken)
    {
        var collections = await ListChromaCollectionsAsync(cancellationToken);
        var metadataSchemas = await LoadMetadataSchemaMapAsync(cancellationToken);
        var collectionInfo = await LoadCollectionInfoMapAsync(cancellationToken);
        var details = new List<VectorStoreCollectionDetails>();

        foreach (var collection in collections
            .Where(collection => !IsSystemCollection(collection.Name))
            .GroupBy(collection => collection.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            details.Add(new VectorStoreCollectionDetails(
                collection.Name,
                await GetCollectionCountAsync(collection.Id, cancellationToken),
                metadataSchemas.GetValueOrDefault(collection.Name) ?? [],
                collectionInfo.GetValueOrDefault(collection.Name) ?? new Dictionary<string, object?>()));
        }

        return details;
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

    public async Task<bool> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _http.GetAsync(CollectionsUrl, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
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

    public bool SupportsGeneratedFilters => false;
    public GeneratedFilterPromptKind GeneratedFilterPromptKind => GeneratedFilterPromptKind.None;

    public Task<string> GetFilterSchemaDescriptionAsync(
        string collectionName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

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
        var metadatas = new List<Dictionary<string, object?>>(documents.Count);

        foreach (var doc in documents)
        {
            ids.Add(doc.Id);
            texts.Add(doc.Text);

            var vec = doc.Embedding?.Count > 0
                ? doc.Embedding
                : await _embedder.EmbedAsync(doc.Text, cancellationToken);
            embeddings.Add(vec);

            var metadata = doc.Metadata is null
                ? new Dictionary<string, object?>()
                : doc.Metadata.ToDictionary(
                    kv => kv.Key,
                    kv => (object?)NormalizeMetadataValue(kv.Value));
            metadatas.Add(metadata);
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
        => await SearchAsync(collectionName, query, topK, null, cancellationToken);

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collectionName,
        string query,
        int topK,
        string? filterExpr,
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
        var payload = new Dictionary<string, object?>
        {
            ["query_embeddings"] = new[] { queryEmbedding },
            ["n_results"] = topK,
            ["include"] = new[] { "documents", "metadatas", "distances" }
        };
        var where = TryBuildChromaWhere(filterExpr);
        if (where is not null)
        {
            payload["where"] = where;
        }

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
                ?.ToDictionary(kv => kv.Key, kv => ToMetadataString(kv.Value))
                ?? new Dictionary<string, string>();

            results.Add(new VectorSearchResult(id, text, score, meta));
        }

        return results;
    }

    private static Dictionary<string, object?>? TryBuildChromaWhere(string? filterExpr)
    {
        if (string.IsNullOrWhiteSpace(filterExpr))
        {
            return null;
        }

        return TryBuildChromaWhereExpression(TrimBalancedParentheses(filterExpr.Trim()));
    }

    private static Dictionary<string, object?>? TryBuildChromaWhereExpression(string expression)
    {
        var orClauses = SplitLogicalClauses(expression, "OR");
        if (orClauses.Count > 1)
        {
            var filters = new List<Dictionary<string, object?>>();
            foreach (var clause in orClauses)
            {
                var parsed = TryBuildChromaWhereExpression(TrimBalancedParentheses(clause));
                if (parsed is null)
                {
                    return null;
                }

                filters.Add(parsed);
            }

            return new Dictionary<string, object?> { ["$or"] = filters };
        }

        var andClauses = SplitLogicalClauses(expression, "AND");
        if (andClauses.Count > 1)
        {
            var filters = new List<Dictionary<string, object?>>();
            foreach (var clause in andClauses)
            {
                var parsed = TryBuildChromaWhereExpression(TrimBalancedParentheses(clause));
                if (parsed is null)
                {
                    return null;
                }

                filters.Add(parsed);
            }

            return new Dictionary<string, object?> { ["$and"] = filters };
        }

        return TryParseSimpleMetadataClause(TrimBalancedParentheses(expression));
    }

    private static IReadOnlyList<string> SplitLogicalClauses(string expression, string logicalOperator)
    {
        var clauses = new List<string>();
        var start = 0;
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < expression.Length; i++)
        {
            var ch = expression[i];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '(')
            {
                depth++;
                continue;
            }

            if (ch == ')')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth == 0 && IsLogicalOperatorAt(expression, i, logicalOperator))
            {
                clauses.Add(expression[start..i].Trim());
                i += logicalOperator.Length - 1;
                start = i + 1;
            }
        }

        clauses.Add(expression[start..].Trim());
        return clauses
            .Where(clause => !string.IsNullOrWhiteSpace(clause))
            .ToList();
    }

    private static bool IsLogicalOperatorAt(string expression, int index, string logicalOperator)
    {
        if (index > 0 && !char.IsWhiteSpace(expression[index - 1]))
        {
            return false;
        }

        if (!expression.AsSpan(index).StartsWith(logicalOperator, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var after = index + logicalOperator.Length;
        return after >= expression.Length || char.IsWhiteSpace(expression[after]);
    }

    private static Dictionary<string, object?>? TryParseSimpleMetadataClause(string clause)
    {
        var match = Regex.Match(
            clause,
            """^\s*(?:content_metadata\[(?:"(?<field>[^"]+)"|'(?<fieldSingle>[^']+)')\]|metadata\[(?:"(?<field2>[^"]+)"|'(?<field2Single>[^']+)')\]|source\[(?:"(?<sourceField>[^"]+)"|'(?<sourceFieldSingle>[^']+)')\]|(?<field3>[A-Za-z0-9_.-]+))\s*(?<op>not\s+in|in|==|!=|>=|<=|>|<)\s*(?<value>.+?)\s*$""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var field = match.Groups["field"].Success
            ? match.Groups["field"].Value
            : match.Groups["fieldSingle"].Success
                ? match.Groups["fieldSingle"].Value
            : match.Groups["field2"].Success
                ? match.Groups["field2"].Value
                : match.Groups["field2Single"].Success
                    ? match.Groups["field2Single"].Value
                    : match.Groups["sourceField"].Success
                        ? $"source.{match.Groups["sourceField"].Value}"
                        : match.Groups["sourceFieldSingle"].Success
                            ? $"source.{match.Groups["sourceFieldSingle"].Value}"
                            : match.Groups["field3"].Value;
        var op = match.Groups["op"].Value;
        var rawValue = match.Groups["value"].Value.Trim();
        if (op.Equals("in", StringComparison.OrdinalIgnoreCase)
            || op.Equals("not in", StringComparison.OrdinalIgnoreCase))
        {
            var values = TryParseChromaList(rawValue);
            if (values is null || values.Count == 0)
            {
                return null;
            }

            return new Dictionary<string, object?>
            {
                [field] = new Dictionary<string, object?>
                {
                    [op.Equals("in", StringComparison.OrdinalIgnoreCase) ? "$in" : "$nin"] = values
                }
            };
        }

        var value = CoerceChromaValue(Unquote(rawValue));

        return op switch
        {
            "==" => new Dictionary<string, object?> { [field] = value },
            "!=" => new Dictionary<string, object?> { [field] = new Dictionary<string, object?> { ["$ne"] = value } },
            ">" => new Dictionary<string, object?> { [field] = new Dictionary<string, object?> { ["$gt"] = value } },
            ">=" => new Dictionary<string, object?> { [field] = new Dictionary<string, object?> { ["$gte"] = value } },
            "<" => new Dictionary<string, object?> { [field] = new Dictionary<string, object?> { ["$lt"] = value } },
            "<=" => new Dictionary<string, object?> { [field] = new Dictionary<string, object?> { ["$lte"] = value } },
            _ => null
        };
    }

    private static IReadOnlyList<object>? TryParseChromaList(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[^1] != ']')
        {
            return null;
        }

        var inner = trimmed[1..^1];
        var values = new List<object>();
        foreach (var part in SplitListValues(inner))
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                values.Add(CoerceChromaValue(Unquote(part.Trim())));
            }
        }

        return values;
    }

    private static IEnumerable<string> SplitListValues(string value)
    {
        var start = 0;
        var quote = '\0';
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == ',')
            {
                yield return value[start..i];
                start = i + 1;
            }
        }

        yield return value[start..];
    }

    private static string Unquote(string raw)
    {
        var trimmed = raw.Trim();
        return trimmed.Length >= 2
               && ((trimmed[0] == '"' && trimmed[^1] == '"')
                   || (trimmed[0] == '\'' && trimmed[^1] == '\''))
            ? trimmed[1..^1]
            : trimmed;
    }

    private static object CoerceChromaValue(string raw)
    {
        if (bool.TryParse(raw, out var boolValue))
        {
            return boolValue;
        }

        if (long.TryParse(raw, out var intValue))
        {
            return intValue;
        }

        if (double.TryParse(raw, out var doubleValue))
        {
            return doubleValue;
        }

        return raw;
    }

    private static string TrimBalancedParentheses(string value)
    {
        var trimmed = value.Trim();
        while (trimmed.Length >= 2
               && trimmed[0] == '('
               && trimmed[^1] == ')'
               && HasBalancedOuterParentheses(trimmed))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private static bool HasBalancedOuterParentheses(string value)
    {
        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '(')
            {
                depth++;
            }
            else if (value[i] == ')')
            {
                depth--;
                if (depth == 0 && i < value.Length - 1)
                {
                    return false;
                }
            }

            if (depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
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

    private async Task<IReadOnlyList<ChromaCollectionRef>> ListChromaCollectionsAsync(
        CancellationToken cancellationToken)
    {
        var response = await _http.GetAsync($"{CollectionsUrl}", cancellationToken);
        response.EnsureSuccessStatusCode();
        var root = await response.Content.ReadFromJsonAsync<JsonArray>(cancellationToken: cancellationToken);
        if (root is null)
        {
            return [];
        }

        return root
            .Select(node =>
            {
                var name = node?["name"]?.GetValue<string>();
                var id = node?["id"]?.GetValue<string>() ?? name;
                return string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)
                    ? null
                    : new ChromaCollectionRef(id, name);
            })
            .Where(item => item is not null)
            .Cast<ChromaCollectionRef>()
            .ToList();
    }

    private async Task<long> GetCollectionCountAsync(
        string collectionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var countResponse = await _http.GetAsync(
                $"{CollectionsUrl}/{collectionId}/count",
                cancellationToken);
            if (countResponse.IsSuccessStatusCode)
            {
                var countBody = await countResponse.Content.ReadAsStringAsync(cancellationToken);
                if (long.TryParse(
                    countBody.Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var directCount))
                {
                    return directCount;
                }
            }

            var payload = new { include = Array.Empty<string>() };
            var getResponse = await PostJsonAsync($"{CollectionsUrl}/{collectionId}/get", payload, cancellationToken);
            if (!getResponse.IsSuccessStatusCode)
            {
                return 0;
            }

            var body = await getResponse.Content.ReadAsStringAsync(cancellationToken);
            var ids = JsonNode.Parse(body)?["ids"]?.AsArray();
            return ids?.Count ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to retrieve ChromaDB collection count for id '{CollectionId}'", collectionId);
            return 0;
        }
    }

    private async Task<Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>> LoadMetadataSchemaMapAsync(
        CancellationToken cancellationToken)
    {
        var collectionId = await TryGetCollectionIdAsync(MetadataSchemaCollection, cancellationToken);
        if (collectionId is null)
        {
            return [];
        }

        var root = await TryGetCollectionRowsAsync(
            collectionId,
            ["metadatas", "documents"],
            cancellationToken);
        var metadatas = root?["metadatas"]?.AsArray();
        var documents = root?["documents"]?.AsArray();
        if (metadatas is null)
        {
            return [];
        }

        var map = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < metadatas.Count; i++)
        {
            var collectionName = GetString(metadatas[i], "collection_name")
                ?? GetString(metadatas[i], "collection");
            if (string.IsNullOrWhiteSpace(collectionName))
            {
                continue;
            }

            var schema = ToObjectList(GetMetadataProperty(metadatas[i], "metadata_schema"))
                ?? ToObjectList(documents is { Count: > 0 } && i < documents.Count ? documents[i] : null);
            if (schema is { Count: > 0 })
            {
                map[collectionName] = schema;
            }
        }

        return map;
    }

    private async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> LoadCollectionInfoMapAsync(
        CancellationToken cancellationToken)
    {
        var collectionId = await TryGetCollectionIdAsync(DocumentInfoCollection, cancellationToken);
        if (collectionId is null)
        {
            return [];
        }

        var root = await TryGetCollectionRowsAsync(
            collectionId,
            ["metadatas", "documents"],
            cancellationToken);
        var metadatas = root?["metadatas"]?.AsArray();
        var documents = root?["documents"]?.AsArray();
        if (metadatas is null)
        {
            return [];
        }

        var map = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < metadatas.Count; i++)
        {
            var collectionName = GetString(metadatas[i], "collection_name")
                ?? GetString(metadatas[i], "collection");
            var infoType = GetString(metadatas[i], "info_type");
            if (string.IsNullOrWhiteSpace(collectionName)
                || !IsCollectionInfoType(infoType))
            {
                continue;
            }

            var info = ToObjectDictionary(GetMetadataProperty(metadatas[i], "info_value"));
            if (info.Count == 0)
            {
                info = ToObjectDictionary(documents is { Count: > 0 } && i < documents.Count ? documents[i] : null);
            }

            if (info.Count == 0)
            {
                continue;
            }

            if (!map.TryGetValue(collectionName, out var existing))
            {
                existing = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                map[collectionName] = existing;
            }

            foreach (var pair in info)
            {
                existing[pair.Key] = pair.Value;
            }
        }

        return map.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, object?>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<JsonNode?> TryGetCollectionRowsAsync(
        string collectionId,
        IReadOnlyList<string> include,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await PostJsonAsync(
                $"{CollectionsUrl}/{collectionId}/get",
                new { include },
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to retrieve ChromaDB rows for collection id '{CollectionId}'", collectionId);
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

    private static object NormalizeMetadataValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string or bool or int or long or float or double or decimal => value,
            _ => JsonSerializer.Serialize(value, JsonOpts)
        };
    }

    private static string ToMetadataString(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var stringValue))
            {
                return stringValue;
            }

            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue ? "true" : "false";
            }

            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<double>(out var doubleValue))
            {
                return doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return node.ToJsonString(JsonOpts);
    }

    private static bool IsSystemCollection(string collectionName)
        => collectionName.Equals(MetadataSchemaCollection, StringComparison.OrdinalIgnoreCase)
           || collectionName.Equals(DocumentInfoCollection, StringComparison.OrdinalIgnoreCase);

    private static bool IsCollectionInfoType(string? infoType)
        => infoType is not null
           && (infoType.Equals("catalog", StringComparison.OrdinalIgnoreCase)
               || infoType.Equals("collection", StringComparison.OrdinalIgnoreCase));

    private static string? GetString(JsonNode? node, string key)
    {
        var value = GetMetadataProperty(node, key);
        return value switch
        {
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text) => text,
            _ => null
        };
    }

    private static JsonNode? GetMetadataProperty(JsonNode? node, string key)
        => node is JsonObject obj && obj.TryGetPropertyValue(key, out var value)
            ? value
            : null;

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>>? ToObjectList(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            try
            {
                node = JsonNode.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        return node is JsonArray array
            ? array
                .Select(ToObjectDictionary)
                .Where(item => item.Count > 0)
                .Cast<IReadOnlyDictionary<string, object?>>()
                .ToList()
            : null;
    }

    private static Dictionary<string, object?> ToObjectDictionary(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            try
            {
                node = JsonNode.Parse(text);
            }
            catch
            {
                return [];
            }
        }

        if (node is not JsonObject obj)
        {
            return [];
        }

        return obj.ToDictionary(
            pair => pair.Key,
            pair => ToClrValue(pair.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private static object? ToClrValue(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonArray array)
        {
            return array.Select(ToClrValue).ToList();
        }

        if (node is JsonObject obj)
        {
            return obj.ToDictionary(
                pair => pair.Key,
                pair => ToClrValue(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue;
            }

            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue;
            }

            if (value.TryGetValue<double>(out var doubleValue))
            {
                return doubleValue;
            }

            if (value.TryGetValue<string>(out var stringValue))
            {
                return stringValue;
            }
        }

        return node.ToJsonString(JsonOpts);
    }

    private sealed record ChromaCollectionRef(string Id, string Name);
}
