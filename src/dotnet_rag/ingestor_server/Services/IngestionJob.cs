using DotnetRag.Ingestor.Models;

namespace DotnetRag.Ingestor.Services;

public sealed class IngestionJob
{
    public string TaskId { get; set; } = string.Empty;
    public DocumentUploadRequest Payload { get; set; } = new();
    public List<string> FilePaths { get; set; } = [];
    public List<Dictionary<string, object?>> ValidationErrors { get; set; } = [];
    public bool IsUpdate { get; set; }
    public string? BearerToken { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
