using System.Text.Json;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Models;
using DotnetRag.Shared.Prompts;

namespace DotnetRag.Rag.Services;

public sealed record VlmContextAssemblyRequest(
    IReadOnlyList<Message> Messages,
    IReadOnlyList<VectorSearchResult> ContextChunks,
    IReadOnlyList<VlmContextAsset> ContextAssets,
    string SystemPrompt,
    string HumanTemplate,
    int MaxTotalImages,
    bool IncludeSourceMetadata);

public sealed record VlmContextAsset(
    string ContentBase64,
    string DocumentType,
    string? Source,
    int? PageNumber,
    string? Caption);

public interface IVlmContextAssembler
{
    IReadOnlyList<ChatMessage> Assemble(VlmContextAssemblyRequest request);
}

public sealed class VlmContextAssembler : IVlmContextAssembler
{
    public IReadOnlyList<ChatMessage> Assemble(VlmContextAssemblyRequest request)
    {
        var existingImageCount = CountUserImages(request.Messages);
        var remainingContextImages = request.MaxTotalImages <= 0
            ? 0
            : Math.Max(0, request.MaxTotalImages - existingImageCount);
        var result = new List<ChatMessage>
        {
            new("system", request.SystemPrompt)
        };

        if (request.ContextChunks.Count > 0)
        {
            var values = new Dictionary<string, string?>
            {
                ["context"] = BuildContextString(request.ContextChunks, request.IncludeSourceMetadata),
                ["question"] = ExtractLastUserText(request.Messages)
            };
            result.Add(new ChatMessage(
                "user",
                BuildContextMessageContent(
                    PromptCatalog.Render(request.HumanTemplate, values),
                    request.ContextAssets,
                    remainingContextImages)));
        }

        foreach (var message in request.Messages)
        {
            result.Add(ConvertMessage(message));
        }

        return result;
    }

    private static ChatMessage ConvertMessage(Message message)
    {
        if (message.Content is not JsonElement element || element.ValueKind != JsonValueKind.Array)
        {
            return new ChatMessage(message.Role, ExtractTextContent(message.Content));
        }

        var parts = new List<object>();
        foreach (var part in element.EnumerateArray())
        {
            if (!part.TryGetProperty("type", out var typeElement))
            {
                continue;
            }

            if (typeElement.GetString() == "image_url")
            {
                parts.Add(JsonSerializer.Deserialize<object>(part.GetRawText())!);
                continue;
            }

            parts.Add(JsonSerializer.Deserialize<object>(part.GetRawText())!);
        }

        return new ChatMessage(message.Role, parts);
    }

    private static object BuildContextMessageContent(
        string renderedText,
        IReadOnlyList<VlmContextAsset> contextAssets,
        int remainingContextImages)
    {
        if (remainingContextImages <= 0 || contextAssets.Count == 0)
        {
            return renderedText;
        }

        var parts = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = renderedText
            }
        };

        foreach (var part in BuildAssetParts(contextAssets, remainingContextImages))
        {
            parts.Add(part);
        }

        return parts;
    }

    private static IEnumerable<object> BuildAssetParts(
        IReadOnlyList<VlmContextAsset> contextAssets,
        int remainingContextImages)
    {
        var emittedImages = 0;
        foreach (var group in GroupAssetsByPage(contextAssets))
        {
            if (emittedImages >= remainingContextImages)
            {
                yield break;
            }

            var assets = group.Assets
                .Take(remainingContextImages - emittedImages)
                .ToList();
            if (assets.Count == 0)
            {
                continue;
            }

            var label = BuildAssetGroupLabel(group.Source, group.PageNumber, assets);
            if (!string.IsNullOrWhiteSpace(label))
            {
                yield return new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = label
                };
            }

            foreach (var asset in assets)
            {
                yield return new Dictionary<string, object?>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object?>
                    {
                        ["url"] = $"data:image/png;base64,{asset.ContentBase64}"
                    }
                };
                emittedImages++;
            }
        }
    }

    private sealed record VlmContextAssetGroup(
        string Source,
        int? PageNumber,
        IReadOnlyList<VlmContextAsset> Assets);

    private static IReadOnlyList<VlmContextAssetGroup> GroupAssetsByPage(
        IReadOnlyList<VlmContextAsset> contextAssets)
    {
        var groups = contextAssets
            .Select((asset, index) => new { Asset = asset, Index = index })
            .GroupBy(item => new
            {
                Source = item.Asset.Source ?? string.Empty,
                item.Asset.PageNumber
            })
            .OrderBy(group => group.Key.PageNumber.HasValue ? 0 : 1)
            .ThenBy(group => group.Key.Source, StringComparer.Ordinal)
            .ThenBy(group => group.Key.PageNumber ?? int.MaxValue)
            .ThenBy(group => group.Min(item => item.Index))
            .Select(group => new VlmContextAssetGroup(
                group.Key.Source,
                group.Key.PageNumber,
                group.OrderBy(item => item.Index).Select(item => item.Asset).ToList()))
            .ToList();

        return groups;
    }

    private static string BuildAssetGroupLabel(
        string source,
        int? pageNumber,
        IReadOnlyList<VlmContextAsset> assets)
    {
        var captions = assets
            .Select(asset => asset.Caption)
            .Where(caption => !string.IsNullOrWhiteSpace(caption))
            .ToList();
        var captionText = captions.Count == 0
            ? string.Empty
            : "\n" + string.Join("\n\n", captions);

        if (pageNumber is not null and > 0)
        {
            var fileName = string.IsNullOrWhiteSpace(source)
                ? "unknown"
                : Path.GetFileNameWithoutExtension(source);
            return $"=== Page {pageNumber} ({fileName}) ==={captionText}";
        }

        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(source))
        {
            details.Add(source);
        }

        var prefix = details.Count > 0
            ? $"Retrieved visual context ({string.Join(", ", details)}):"
            : "Retrieved visual context:";
        return $"{prefix}{captionText}";
    }

    private static int CountUserImages(IReadOnlyList<Message> messages)
    {
        var count = 0;
        foreach (var message in messages)
        {
            if (message.Content is not JsonElement element || element.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            count += element.EnumerateArray()
                .Count(part => part.TryGetProperty("type", out var type)
                    && type.GetString() == "image_url");
        }

        return count;
    }

    private static string BuildContextString(
        IReadOnlyList<VectorSearchResult> chunks,
        bool includeSourceMetadata)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < chunks.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("\n---\n");
            }

            if (includeSourceMetadata
                && chunks[i].Metadata?.TryGetValue("filename", out var fileName) == true
                && !string.IsNullOrEmpty(fileName))
            {
                sb.Append($"[Source: {fileName}]\n");
            }

            sb.Append(chunks[i].Text);
        }

        return sb.ToString();
    }

    private static string ExtractLastUserText(IReadOnlyList<Message> messages) =>
        ExtractTextContent(messages.LastOrDefault(message =>
            string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty);

    private static string ExtractTextContent(object content)
    {
        return content switch
        {
            string value => value,
            JsonElement element when element.ValueKind == JsonValueKind.String
                => element.GetString() ?? string.Empty,
            JsonElement element when element.ValueKind == JsonValueKind.Array
                => string.Join(" ", element.EnumerateArray()
                    .Where(part => part.TryGetProperty("type", out var type) && type.GetString() == "text")
                    .Select(part => part.TryGetProperty("text", out var text) ? text.GetString() : null)
                    .Where(text => !string.IsNullOrWhiteSpace(text))),
            _ => content.ToString() ?? string.Empty
        };
    }
}
