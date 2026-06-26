using System.Collections;
using System.Text.Json;
using DotnetRag.Ingestor.Models;

namespace DotnetRag.Ingestor.Services;

public static class SummaryOptionsValidator
{
    private static readonly HashSet<string> AllowedStrategies =
        new(StringComparer.Ordinal) { "single", "hierarchical" };

    public static string? ValidateAndNormalize(bool generateSummary, SummaryOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        if (!generateSummary)
        {
            return "summary_options can only be provided when generate_summary=True. "
                + "Either set generate_summary=True or remove summary_options.";
        }

        var pageFilterError = ValidateAndNormalizePageFilter(options);
        if (pageFilterError is not null)
        {
            return pageFilterError;
        }

        if (options.SummarizationStrategy is not null)
        {
            var strategy = options.SummarizationStrategy.Trim();
            if (!AllowedStrategies.Contains(strategy))
            {
                return $"Invalid summarization_strategy: '{options.SummarizationStrategy}'. "
                    + "Allowed values: ['single', 'hierarchical']";
            }

            options.SummarizationStrategy = strategy;
        }

        return null;
    }

    private static string? ValidateAndNormalizePageFilter(SummaryOptions options)
    {
        if (options.PageFilter is null)
        {
            return null;
        }

        if (options.PageFilter is string raw)
        {
            var value = raw.Trim().ToLowerInvariant();
            if (value is not ("even" or "odd"))
            {
                return $"Invalid page_filter string '{raw}'. Supported: 'even', 'odd'";
            }

            options.PageFilter = value;
            return null;
        }

        if (options.PageFilter is JsonElement jsonElement)
        {
            return ValidateJsonElementPageFilter(jsonElement);
        }

        if (options.PageFilter is IEnumerable enumerable)
        {
            return ValidateEnumerablePageFilter(enumerable);
        }

        return $"Invalid page_filter type: {options.PageFilter.GetType().Name}. "
            + "Expected: list[list[int]] (ranges) or str ('even'/'odd')";
    }

    private static string? ValidateJsonElementPageFilter(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString() ?? string.Empty;
            var value = raw.Trim().ToLowerInvariant();
            if (value is not ("even" or "odd"))
            {
                return $"Invalid page_filter string '{raw}'. Supported: 'even', 'odd'";
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return $"Invalid page_filter type: {element.ValueKind}. "
                + "Expected: list[list[int]] (ranges) or str ('even'/'odd')";
        }

        var ranges = element.EnumerateArray().ToList();
        if (ranges.Count == 0)
        {
            return "Page filter range list cannot be empty";
        }

        for (var i = 0; i < ranges.Count; i++)
        {
            var range = ranges[i];
            if (range.ValueKind != JsonValueKind.Array)
            {
                return "Page filter must contain ranges as [start, end]. "
                    + "Got mixed types or non-list items.";
            }

            var items = range.EnumerateArray().ToList();
            if (items.Count != 2)
            {
                return $"Range {i} must have exactly 2 elements [start, end], got {items.Count}";
            }

            if (items[0].ValueKind != JsonValueKind.Number
                || items[1].ValueKind != JsonValueKind.Number
                || !items[0].TryGetInt32(out var start)
                || !items[1].TryGetInt32(out var end))
            {
                return $"Range {i} must contain integers";
            }

            var rangeError = ValidateRange(i, start, end);
            if (rangeError is not null)
            {
                return rangeError;
            }
        }

        return null;
    }

    private static string? ValidateEnumerablePageFilter(IEnumerable rangesEnumerable)
    {
        var ranges = rangesEnumerable.Cast<object?>().ToList();
        if (ranges.Count == 0)
        {
            return "Page filter range list cannot be empty";
        }

        for (var i = 0; i < ranges.Count; i++)
        {
            if (ranges[i] is not IEnumerable rangeEnumerable || ranges[i] is string)
            {
                return "Page filter must contain ranges as [start, end]. "
                    + "Got mixed types or non-list items.";
            }

            var items = rangeEnumerable.Cast<object?>().ToList();
            if (items.Count != 2)
            {
                return $"Range {i} must have exactly 2 elements [start, end], got {items.Count}";
            }

            if (!TryConvertInt(items[0], out var start) || !TryConvertInt(items[1], out var end))
            {
                return $"Range {i} must contain integers";
            }

            var rangeError = ValidateRange(i, start, end);
            if (rangeError is not null)
            {
                return rangeError;
            }
        }

        return null;
    }

    private static string? ValidateRange(int i, int start, int end)
    {
        if (start == 0 || end == 0)
        {
            return $"Range {i}: page numbers cannot be 0. "
                + "Use 1-based indexing or negative for last pages.";
        }

        if (start < 0 && end < 0 && start > end)
        {
            return $"Range {i}: invalid negative range [{start}, {end}]. "
                + "Use [-10, -1] for last 10 pages, not [-1, -10].";
        }

        if (start > 0 && end > 0 && start > end)
        {
            return $"Range {i}: start must be <= end, got [{start}, {end}]";
        }

        if ((start < 0 && end > 0) || (start > 0 && end < 0))
        {
            return $"Range {i}: cannot mix positive and negative indexing in same range. "
                + $"Got [{start}, {end}]";
        }

        return null;
    }

    private static bool TryConvertInt(object? value, out int result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } json:
                return json.TryGetInt32(out result);
            default:
                result = 0;
                return false;
        }
    }
}
