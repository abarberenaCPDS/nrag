using DotnetRag.Ingestor.Models;

namespace dotnet_rag.ingestor_server;

public class MultipartUploadParseResult(
    bool success,
    IResult? errorResult,
    IReadOnlyList<IFormFile>? files,
    DocumentUploadRequest? payload)
{
    public bool Success { get; } = success;
    public IResult? ErrorResult { get; } = errorResult;
    public IReadOnlyList<IFormFile>? Files { get; } = files;
    public DocumentUploadRequest? Payload { get; } = payload;
}