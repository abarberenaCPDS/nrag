using DotnetRag.Ingestor.Models;

namespace DotnetRag.Ingestor.Services;

public sealed record IngestionPipelineResult(
    string Text,
    IReadOnlyDictionary<string, object?> DocumentInfo,
    IReadOnlyList<string> AssetObjectNames);

public interface IIngestionPipeline
{
    string BackendName { get; }
    bool SupportsMultimodalExtraction { get; }
    bool SupportsObjectStoreAssets { get; }
    string SupportedFileTypesMessage => IngestionFileTypes.ExternalSupportedTypesMessage;

    bool SupportsFile(string filename) =>
        IngestionFileTypes.ExternalSupportedExtensions.Contains(
            Path.GetExtension(filename).ToLowerInvariant());

    async Task<IngestionPipelineResult> ExtractAsync(
        string path,
        string filename,
        ExtractionOptions? extractionOptions = null,
        CancellationToken cancellationToken = default)
    {
        var text = extractionOptions?.ExtractText == false
            ? string.Empty
            : await ExtractTextAsync(path, filename, cancellationToken);
        return new IngestionPipelineResult(text, new Dictionary<string, object?>(), []);
    }

    Task<string> ExtractTextAsync(
        string path,
        string filename,
        CancellationToken cancellationToken = default);
}

public static class IngestionFileTypes
{
    public static readonly IReadOnlySet<string> LocalSupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt",
            ".text",
            ".md",
            ".markdown",
            ".pdf",
            ".docx",
            ".pptx",
            ".html",
            ".htm"
        };

    public static readonly IReadOnlySet<string> ExternalSupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt",
            ".text",
            ".md",
            ".markdown",
            ".pdf",
            ".docx",
            ".pptx",
            ".html",
            ".htm",
            ".csv",
            ".json",
            ".xml",
            ".jpg",
            ".jpeg",
            ".png",
            ".bmp",
            ".tif",
            ".tiff",
            ".mp3",
            ".wav",
            ".mp4"
        };

    public static string LocalSupportedTypesMessage =>
        string.Join(", ", LocalSupportedExtensions
            .Select(ext => ext.TrimStart('.'))
            .Order(StringComparer.OrdinalIgnoreCase));

    public static string ExternalSupportedTypesMessage =>
        string.Join(", ", ExternalSupportedExtensions
            .Select(ext => ext.TrimStart('.'))
            .Order(StringComparer.OrdinalIgnoreCase));
}
