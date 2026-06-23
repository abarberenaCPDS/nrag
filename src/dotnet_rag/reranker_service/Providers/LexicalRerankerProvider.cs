using System.Text.RegularExpressions;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Models;

namespace DotnetRag.Reranker.Providers;

public sealed class LexicalRerankerProvider : IRerankerProvider
{
    private static readonly Regex TokenRegex =
        new("[a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name => "lexical";

    public Task<IReadOnlyList<RerankChunkResult>> RerankAsync(
        RerankRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var queryTokens = Tokenize(request.Query);
        var reranked = request.Chunks
            .Select(chunk =>
            {
                var docTokens = Tokenize(chunk.Text);
                var overlap = queryTokens.Count == 0
                    ? 0.0
                    : queryTokens.Count(t => docTokens.Contains(t)) / (double)queryTokens.Count;

                var lexicalScore = (0.85 * overlap) + (0.15 * Math.Max(0, chunk.Score));

                return new RerankChunkResult(
                    Id: chunk.Id,
                    Text: chunk.Text,
                    OriginalScore: chunk.Score,
                    RelevanceScore: lexicalScore,
                    Metadata: chunk.Metadata);
            })
            .OrderByDescending(r => r.RelevanceScore)
            .Take(request.TopK)
            .ToList();

        return Task.FromResult<IReadOnlyList<RerankChunkResult>>(reranked);
    }

    private static HashSet<string> Tokenize(string input)
    {
        return TokenRegex.Matches(input.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(token => token.Length > 1)
            .ToHashSet(StringComparer.Ordinal);
    }
}
