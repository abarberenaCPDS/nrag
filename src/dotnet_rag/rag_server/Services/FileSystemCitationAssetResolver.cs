using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Abstractions;
using System.Text.Json;

namespace DotnetRag.Rag.Services;

public sealed class FileSystemCitationAssetResolver(
    RagServerConfiguration config,
    ILogger<FileSystemCitationAssetResolver> logger) : ICitationAssetResolver
{
    private static readonly HashSet<string> VisualDocumentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image",
        "table",
        "chart",
        "structured",
        "image_caption"
    };

    public async Task<CitationAsset?> ResolveAsync(
        VectorSearchResult result,
        CancellationToken cancellationToken = default)
    {
        var metadata = result.Metadata;
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        var documentType = ResolveDocumentType(metadata);
        if (!VisualDocumentTypes.Contains(documentType))
        {
            return null;
        }

        var assetLocation = ResolveAssetLocation(metadata);
        if (string.IsNullOrWhiteSpace(assetLocation))
        {
            return null;
        }

        if (TryExtractDataUriBase64(assetLocation, out var inlineBase64))
        {
            return new CitationAsset(inlineBase64, NormalizeVisualType(documentType));
        }

        var path = ResolveLocalPath(assetLocation);
        if (path is null || !File.Exists(path))
        {
            logger.LogDebug("Citation asset was not found at {AssetLocation}", assetLocation);
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return new CitationAsset(Convert.ToBase64String(bytes), NormalizeVisualType(documentType));
    }

    private string? ResolveLocalPath(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                return uri.LocalPath;
            }

            if (string.Equals(uri.Scheme, "s3", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(config.ObjectStoreRoot))
            {
                var objectPath = uri.AbsolutePath.TrimStart('/');
                return Path.Combine(config.ObjectStoreRoot, uri.Host, objectPath);
            }

            return null;
        }

        if (Path.IsPathRooted(value))
        {
            return value;
        }

        return string.IsNullOrWhiteSpace(config.ObjectStoreRoot)
            ? null
            : Path.Combine(config.ObjectStoreRoot, value.TrimStart('/', '\\'));
    }

    private static bool TryExtractDataUriBase64(string value, out string contentBase64)
    {
        contentBase64 = string.Empty;
        var trimmed = value.Trim();
        const string marker = ";base64,";
        if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var index = trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        contentBase64 = trimmed[(index + marker.Length)..].Trim();
        return !string.IsNullOrWhiteSpace(contentBase64);
    }

    private static string ResolveDocumentType(IReadOnlyDictionary<string, string> metadata)
    {
        var subtype = FirstMetadataValue(
            metadata,
            "content_metadata.subtype",
            "subtype",
            "document_subtype");
        if (!string.IsNullOrWhiteSpace(subtype)
            && (subtype.Equals("table", StringComparison.OrdinalIgnoreCase)
                || subtype.Equals("chart", StringComparison.OrdinalIgnoreCase)))
        {
            return subtype;
        }

        return FirstMetadataValue(
            metadata,
            "document_type",
            "content_metadata.type",
            "content_metadata",
            "type",
            "content_type") ?? "text";
    }

    private static string NormalizeVisualType(string documentType) =>
        documentType.Equals("image_caption", StringComparison.OrdinalIgnoreCase)
            ? "image"
            : documentType;

    private static string? ResolveAssetLocation(IReadOnlyDictionary<string, string> metadata)
    {
        var directLocation = FirstMetadataValue(
            metadata,
            "stored_image_uri",
            "source_location",
            "source.source_location",
            "source",
            "thumbnail_id",
            "thumbnail_uri",
            "thumbnail_object_name",
            "source.thumbnail_id",
            "source.thumbnail_uri",
            "source.thumbnail_object_name",
            "storage_uri",
            "image_uri",
            "asset_uri");
        if (!string.IsNullOrWhiteSpace(directLocation))
        {
            return directLocation;
        }

        var assetObjectNames = FirstMetadataValue(metadata, "asset_object_names");
        return FirstAssetObjectName(assetObjectNames);
    }

    private static string? FirstAssetObjectName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using var json = JsonDocument.Parse(trimmed);
                if (json.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in json.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(item.GetString()))
                        {
                            return item.GetString();
                        }

                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            var objectLocation = FirstJsonProperty(
                                item,
                                "object_name",
                                "asset_object_name",
                                "thumbnail_object_name",
                                "uri",
                                "url",
                                "path",
                                "source_location");
                            if (!string.IsNullOrWhiteSpace(objectLocation))
                            {
                                return objectLocation;
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }

            return null;
        }

        return trimmed
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    private static string? FirstMetadataValue(
        IReadOnlyDictionary<string, string> metadata,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                var nested = key switch
                {
                    "content_metadata" => FirstJsonProperty(value, "subtype", "type", "document_type"),
                    "source" => FirstJsonProperty(
                        value,
                        "source_location",
                        "stored_image_uri",
                        "thumbnail_id",
                        "thumbnail_uri",
                        "thumbnail_object_name",
                        "storage_uri",
                        "image_uri",
                        "asset_uri"),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }

                return value;
            }
        }

        return null;
    }

    private static string? FirstJsonProperty(string value, params string[] keys)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(trimmed);
            return json.RootElement.ValueKind == JsonValueKind.Object
                ? FirstJsonProperty(json.RootElement, keys)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FirstJsonProperty(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.GetString()))
            {
                return property.GetString();
            }

            if (property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return property.ToString();
            }
        }

        return null;
    }
}
