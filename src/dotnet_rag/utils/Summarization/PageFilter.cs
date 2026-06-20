using System.Text.Json;

namespace DotnetRag.Shared.Summarization;

// ORIG: nvidia_rag/utils/summarization.py::matches_page_filter
// Supports: null (all pages), string "even"/"odd", JsonElement array [[start,end],...]
public static class PageFilter
{
    public static bool Matches(int pageNumber, object? filter, int? totalPages = null)
    {
        return filter switch
        {
            null => true,
            string s => MatchesParity(pageNumber, s),
            JsonElement { ValueKind: JsonValueKind.String } el
                => MatchesParity(pageNumber, el.GetString() ?? string.Empty),
            JsonElement { ValueKind: JsonValueKind.Array } el
                => MatchesRanges(pageNumber, el, totalPages),
            _ => true
        };
    }

    private static bool MatchesParity(int page, string parity) =>
        parity.Trim().ToLowerInvariant() switch
        {
            "even" => page % 2 == 0,
            "odd" => page % 2 != 0,
            _ => false
        };

    private static bool MatchesRanges(int page, JsonElement rangesEl, int? totalPages)
    {
        foreach (var range in rangesEl.EnumerateArray())
        {
            if (range.ValueKind != JsonValueKind.Array) continue;
            var items = range.EnumerateArray().ToList();
            if (items.Count < 2) continue;

            int start = items[0].GetInt32();
            int end = items[1].GetInt32();

            if (totalPages.HasValue)
            {
                // Resolve negative indices (Python-style: -1 = last page)
                if (start <= 0) start = totalPages.Value + start + 1;
                if (end <= 0) end = totalPages.Value + end + 1;
                start = Math.Clamp(start, 1, totalPages.Value);
                end = Math.Clamp(end, 1, totalPages.Value);
            }

            if (start <= page && page <= end) return true;
        }

        return false;
    }
}
