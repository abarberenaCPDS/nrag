using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Models;

namespace DotnetRag.Rag.Services;

public sealed class UnavailableAgenticRagService(
    RagServerConfiguration config,
    ILogger<UnavailableAgenticRagService> logger) : IAgenticRagService
{
    public bool IsRequested(Prompt prompt) =>
        prompt.Agentic == true || (prompt.Agentic is null && config.EnableAgenticRag);

    public Task<IResult> GenerateAsync(
        AgenticRagInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Agentic RAG request rejected for path {Path} because no enabled Agentic RAG runtime is registered.",
            invocation.RequestPath);
        return Task.FromResult<IResult>(Results.Json(
            new { message = "Agentic RAG is not enabled in the .NET RAG server." },
            statusCode: StatusCodes.Status501NotImplemented));
    }
}
