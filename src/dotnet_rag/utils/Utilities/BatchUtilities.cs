namespace DotnetRag.Shared.Utilities;

public static class BatchUtilities
{
    private static readonly HashSet<string> TextLikeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt",
        "md",
        "json",
        "sh",
        "html"
    };

    public static (int FilesPerBatch, int ConcurrentBatches) CalculateDynamicBatchParameters(
        IReadOnlyList<string> filepaths,
        int defaultFilesPerBatch,
        int defaultConcurrentBatches)
    {
        if (filepaths.Count == 0)
        {
            return (defaultFilesPerBatch, defaultConcurrentBatches);
        }

        var textLikeCount = filepaths.Count(path =>
            TextLikeExtensions.Contains(Path.GetExtension(path).TrimStart('.')));
        var percentage = textLikeCount * 100.0 / filepaths.Count;

        return percentage > 50.0
            ? (Math.Min(16, defaultFilesPerBatch), 4)
            : (defaultFilesPerBatch, defaultConcurrentBatches);
    }
}

