using System.Text;
using System.Text.RegularExpressions;
using DotnetRag.Ingestor.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DotnetRag.Ingestor.Services;

public sealed class LocalIngestionPipeline : IIngestionPipeline
{
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);

    public string BackendName => "local";
    public bool SupportsMultimodalExtraction => false;
    public bool SupportsObjectStoreAssets => false;
    public string SupportedFileTypesMessage => IngestionFileTypes.LocalSupportedTypesMessage;

    public bool SupportsFile(string filename) =>
        IngestionFileTypes.LocalSupportedExtensions.Contains(
            Path.GetExtension(filename).ToLowerInvariant());

    public async Task<IngestionPipelineResult> ExtractAsync(
        string path,
        string filename,
        ExtractionOptions? extractionOptions = null,
        CancellationToken cancellationToken = default)
    {
        var text = extractionOptions?.ExtractText == false
            ? string.Empty
            : await ExtractTextAsync(path, filename, cancellationToken);
        return new IngestionPipelineResult(
            text,
            new Dictionary<string, object?>
            {
                ["total_elements"] = EstimateElementCount(text),
                ["raw_text_elements_size"] = text.Length,
                ["has_tables"] = false,
                ["has_charts"] = false,
                ["has_images"] = false
            },
            []);
    }

    public async Task<string> ExtractTextAsync(
        string path,
        string filename,
        CancellationToken cancellationToken = default)
    {
        return Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".pdf" => ExtractPdfText(path),
            ".docx" => ExtractDocxText(path),
            ".pptx" => ExtractPptxText(path),
            ".html" or ".htm" => ExtractHtmlText(await File.ReadAllTextAsync(path, cancellationToken)),
            _ => await File.ReadAllTextAsync(path, cancellationToken)
        };
    }

    private static string ExtractPdfText(string path)
    {
        using var document = PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            sb.AppendLine(ContentOrderTextExtractor.GetText(page));
        }

        return sb.ToString();
    }

    private static string ExtractDocxText(string path)
    {
        using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, false);
        return doc.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty;
    }

    private static string ExtractPptxText(string path)
    {
        using var prs = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(path, false);
        var sb = new StringBuilder();
        foreach (var slidePart in prs.PresentationPart?.SlideParts ?? [])
        {
            var texts = slidePart.Slide
                .Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                .Select(t => t.Text);
            sb.AppendLine(string.Join(" ", texts));
        }

        return sb.ToString();
    }

    private static string ExtractHtmlText(string html) =>
        HtmlTagRegex.Replace(html, " ").Trim();

    private static int EstimateElementCount(string text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split(
                    ['\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Length;
    }
}
