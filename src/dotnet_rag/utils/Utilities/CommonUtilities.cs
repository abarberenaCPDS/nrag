using System.Text.Json;

namespace DotnetRag.Shared.Utilities;

public static class CommonUtilities
{
    public static IReadOnlyDictionary<string, object?> CombineDictionaries(
        IReadOnlyDictionary<string, object?> left,
        IReadOnlyDictionary<string, object?> right)
    {
        var result = left.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var (key, value) in right)
        {
            if (result.TryGetValue(key, out var existing)
                && existing is IReadOnlyDictionary<string, object?> existingDict
                && value is IReadOnlyDictionary<string, object?> valueDict)
            {
                result[key] = CombineDictionaries(existingDict, valueDict);
            }
            else
            {
                result[key] = value;
            }
        }

        return result;
    }

    public static string SanitizeNimUrl(string url, string modelName, string modelType)
    {
        _ = modelName;
        _ = modelType;
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return $"http://{url}/v1";
    }

    public static IEnumerable<T> FilterDocumentsByConfidence<T>(
        IEnumerable<T> documents,
        Func<T, double?> scoreSelector,
        double confidenceThreshold)
    {
        return documents.Where(doc => (scoreSelector(doc) ?? 0) >= confidenceThreshold);
    }

    public static string ObjectKeyFromStorageUri(string uri)
    {
        if (!uri.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid S3 URI", nameof(uri));
        }

        var path = uri["s3://".Length..];
        var slashIndex = path.IndexOf('/');
        if (slashIndex < 0 || slashIndex == path.Length - 1)
        {
            throw new ArgumentException("Invalid S3 URI format", nameof(uri));
        }

        return path[(slashIndex + 1)..];
    }

    public static void ReleaseNvidiaClientResponse(object _)
    {
        // No-op in the .NET migration scaffold.
    }

    public static string SerializeJson(object value) => JsonSerializer.Serialize(value);
}

