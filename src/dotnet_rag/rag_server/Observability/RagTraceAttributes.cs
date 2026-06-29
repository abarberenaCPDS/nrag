using System.Diagnostics;
using System.Text.Json;

namespace DotnetRag.Rag.Observability;

internal static class RagTraceAttributes
{
    public static void SetLlmUsageTags(
        Activity? activity,
        IReadOnlyDictionary<string, object?>? usage)
    {
        if (activity is null || usage is null)
        {
            return;
        }

        var inputTokens = GetUsageToken(usage, "prompt_tokens", "input_tokens");
        var outputTokens = GetUsageToken(usage, "completion_tokens", "output_tokens");
        var totalTokens = GetUsageToken(usage, "total_tokens");

        if (inputTokens is not null)
        {
            activity.SetTag("gen_ai.usage.input_tokens", inputTokens.Value);
        }

        if (outputTokens is not null)
        {
            activity.SetTag("gen_ai.usage.output_tokens", outputTokens.Value);
        }

        if (totalTokens is null && inputTokens is not null && outputTokens is not null)
        {
            totalTokens = inputTokens + outputTokens;
        }

        if (totalTokens is not null)
        {
            activity.SetTag("llm.usage.total_tokens", totalTokens.Value);
        }
    }

    private static int? GetUsageToken(
        IReadOnlyDictionary<string, object?> usage,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!usage.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            switch (value)
            {
                case int intValue:
                    return intValue;
                case long longValue when longValue <= int.MaxValue && longValue >= int.MinValue:
                    return (int)longValue;
                case JsonElement element when element.ValueKind == JsonValueKind.Number
                    && element.TryGetInt32(out var jsonInt):
                    return jsonInt;
                case string stringValue when int.TryParse(stringValue, out var parsed):
                    return parsed;
            }
        }

        return null;
    }
}
