using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Models;

namespace DotnetRag.Reranker.Services;

public sealed class RerankerOrchestrator(
    IEnumerable<IRerankerProvider> providers,
    RerankerServiceConfiguration config,
    ILogger<RerankerOrchestrator> logger)
{
    private readonly IReadOnlyDictionary<string, IRerankerProvider> _providers =
        providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public async Task<RerankResponse> RerankAsync(
        RerankRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Chunks.Count == 0)
        {
            return new RerankResponse([], ProviderUsed: "none", Message: "No chunks to rerank.");
        }

        var normalizedRequest = request with
        {
            TopK = request.TopK > 0 ? request.TopK : 10,
            Query = request.Query.Trim()
        };

        if (string.Equals(normalizedRequest.Provider, "disabled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(config.PrimaryProvider, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return new RerankResponse(
                Results: PassThrough(normalizedRequest),
                ProviderUsed: "disabled-pass-through",
                Message: "Reranker explicitly disabled.");
        }

        var primaryProviderName = ResolvePrimaryProvider(normalizedRequest.Provider, config);
        if (TryResolve(primaryProviderName, out var primaryProvider))
        {
            try
            {
                var primaryResults = await primaryProvider.RerankAsync(normalizedRequest, cancellationToken);
                return new RerankResponse(
                    Results: primaryResults,
                    ProviderUsed: primaryProvider.Name,
                    FallbackUsed: false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Primary reranker provider '{Provider}' failed. Attempting fallback provider.",
                    primaryProvider.Name);
            }
        }

        var fallbackProviderName = NormalizeProviderName(config.FallbackProvider);
        if (TryResolve(fallbackProviderName, out var fallbackProvider))
        {
            try
            {
                var fallbackResults = await fallbackProvider.RerankAsync(
                    normalizedRequest with { Provider = "fallback" },
                    cancellationToken);
                return new RerankResponse(
                    Results: fallbackResults,
                    ProviderUsed: fallbackProvider.Name,
                    FallbackUsed: true,
                    Message: $"Fallback provider '{fallbackProvider.Name}' used.");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Fallback reranker provider '{Provider}' failed.",
                    fallbackProvider.Name);
            }
        }

        if (config.EnableLexicalEmergencyFallback && TryResolve("lexical", out var lexicalProvider))
        {
            var lexicalResults = await lexicalProvider.RerankAsync(normalizedRequest, cancellationToken);
            return new RerankResponse(
                Results: lexicalResults,
                ProviderUsed: lexicalProvider.Name,
                FallbackUsed: true,
                Message: "Emergency lexical fallback used after provider failures.");
        }

        return new RerankResponse(
            Results: PassThrough(normalizedRequest),
            ProviderUsed: "pass-through",
            FallbackUsed: true,
            Message: "No reranker provider available. Returned original order.");
    }

    private static string ResolvePrimaryProvider(string? requestProvider, RerankerServiceConfiguration config)
    {
        var normalizedRequestProvider = NormalizeProviderName(requestProvider);
        if (!string.IsNullOrWhiteSpace(normalizedRequestProvider))
        {
            return normalizedRequestProvider;
        }

        var configuredProvider = NormalizeProviderName(config.PrimaryProvider);
        if (!string.Equals(configuredProvider, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return configuredProvider;
        }

        if (config.PrimaryEndpoint.Contains("11434", StringComparison.OrdinalIgnoreCase))
        {
            return "ollama";
        }

        return "openai";
    }

    private static string NormalizeProviderName(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return string.Empty;
        }

        var value = provider.Trim().ToLowerInvariant();
        return value switch
        {
            "openai-compatible" => "openai",
            "openai_compatible" => "openai",
            _ => value
        };
    }

    private bool TryResolve(string providerName, out IRerankerProvider provider) =>
        _providers.TryGetValue(providerName, out provider!);

    private static IReadOnlyList<RerankChunkResult> PassThrough(RerankRequest request)
    {
        return request.Chunks
            .OrderByDescending(c => c.Score)
            .Take(request.TopK)
            .Select(c => new RerankChunkResult(
                Id: c.Id,
                Text: c.Text,
                OriginalScore: c.Score,
                RelevanceScore: c.Score,
                Metadata: c.Metadata))
            .ToList();
    }
}
