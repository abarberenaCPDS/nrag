using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DotnetRag.Rag.Services;

public sealed record AgenticJsonParseResult(
    JsonObject? Object,
    string? Error,
    string RawResponse)
{
    public bool Succeeded => Object is not null && Error is null;
}

public sealed record AgenticPlanTask(
    string Id,
    string Question,
    string Query);

public sealed record AgenticPlan(
    bool ScopeOnly,
    string ScopeResolution,
    string ResolvedQuery,
    IReadOnlyList<AgenticPlanTask> Tasks,
    string SynthesisInstruction);

public sealed record AgenticPlanParseResult(
    AgenticPlan? Plan,
    string? Error,
    string RawResponse)
{
    public bool Succeeded => Plan is not null && Error is null;
}

public sealed record AgenticTaskAnswer(
    string Completeness,
    string Answer,
    string Missing);

public sealed record AgenticSeedQuery(
    string Reasoning,
    string? SeedQuery,
    bool Stop);

public sealed record AgenticSeedQueryParseResult(
    AgenticSeedQuery? SeedQuery,
    string? Error,
    string RawResponse)
{
    public bool Succeeded => SeedQuery is not null && Error is null;
}

public sealed record AgenticSynthesisResult(string Answer);

public sealed record AgenticVerification(
    bool Passed,
    string Reasoning,
    IReadOnlyList<string> Issues,
    IReadOnlyList<AgenticPlanTask> Tasks);

public sealed record AgenticVerificationParseResult(
    AgenticVerification? Verification,
    string? Error,
    string RawResponse)
{
    public bool Succeeded => Verification is not null && Error is null;
}

public static partial class AgenticResponseParser
{
    public static AgenticJsonParseResult ParseJsonResponse(string response)
    {
        if (TryParseObject(response, out var direct))
        {
            return new AgenticJsonParseResult(direct, null, response);
        }

        foreach (var candidate in ExtractTopLevelObjects(response).Reverse())
        {
            if (TryParseObject(candidate, out var parsed)
                || TryParseObject(SanitizeJsonString(candidate), out parsed))
            {
                return new AgenticJsonParseResult(parsed, null, response);
            }
        }

        var start = response.IndexOf('{', StringComparison.Ordinal);
        var end = response.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            var broad = response[start..(end + 1)];
            if (TryParseObject(broad, out var parsed)
                || TryParseObject(SanitizeJsonString(broad), out parsed))
            {
                return new AgenticJsonParseResult(parsed, null, response);
            }
        }

        return new AgenticJsonParseResult(null, "Failed to parse JSON", response);
    }

    public static AgenticPlanParseResult ParsePlan(string response)
    {
        var parsed = ParseJsonResponse(response);
        if (!parsed.Succeeded || parsed.Object is null)
        {
            return new AgenticPlanParseResult(null, parsed.Error, response);
        }

        var obj = parsed.Object;
        var tasks = new List<AgenticPlanTask>();
        if (obj["tasks"] is JsonArray taskArray)
        {
            foreach (var taskNode in taskArray)
            {
                if (taskNode is not JsonObject taskObject)
                {
                    return new AgenticPlanParseResult(null, "Task entry must be a JSON object", response);
                }

                var id = GetScalarString(taskObject, "id");
                var query = GetScalarString(taskObject, "query");
                var question = GetScalarString(taskObject, "question") ?? query;
                if (string.IsNullOrWhiteSpace(id)
                    || string.IsNullOrWhiteSpace(question)
                    || string.IsNullOrWhiteSpace(query))
                {
                    return new AgenticPlanParseResult(
                        null,
                        "Task entries must include id, question, and query",
                        response);
                }

                tasks.Add(new AgenticPlanTask(id, question, query));
            }
        }

        var plan = new AgenticPlan(
            ScopeOnly: obj["scope_only"]?.GetValue<bool>() ?? false,
            ScopeResolution: obj["scope_resolution"]?.GetValue<string>() ?? string.Empty,
            ResolvedQuery: obj["resolved_query"]?.GetValue<string>() ?? string.Empty,
            Tasks: tasks,
            SynthesisInstruction: obj["synthesis_instruction"]?.GetValue<string>() ?? string.Empty);

        return new AgenticPlanParseResult(plan, null, response);
    }

    public static AgenticTaskAnswer ParseTaskAnswer(string rawAnswer)
    {
        if (string.IsNullOrWhiteSpace(rawAnswer))
        {
            return new AgenticTaskAnswer("none", "[NO DATA]", string.Empty);
        }

        var parsed = ParseJsonResponse(rawAnswer);
        if (parsed.Succeeded && parsed.Object is not null && parsed.Object.ContainsKey("completeness"))
        {
            return new AgenticTaskAnswer(
                GetString(parsed.Object, "completeness", "complete"),
                CleanAnswer(GetString(parsed.Object, "answer", string.Empty)),
                GetString(parsed.Object, "missing", string.Empty));
        }

        var text = CleanAnswer(rawAnswer.Trim());
        return string.IsNullOrWhiteSpace(text) || text == "[NO DATA]"
            ? new AgenticTaskAnswer("none", "[NO DATA]", string.Empty)
            : new AgenticTaskAnswer("complete", text, string.Empty);
    }

    public static AgenticSeedQueryParseResult ParseSeedQuery(string response)
    {
        var parsed = ParseJsonResponse(response);
        if (!parsed.Succeeded || parsed.Object is null)
        {
            return new AgenticSeedQueryParseResult(null, parsed.Error, response);
        }

        var obj = parsed.Object;
        return new AgenticSeedQueryParseResult(
            new AgenticSeedQuery(
                GetString(obj, "reasoning", string.Empty),
                GetNullableString(obj, "seed_query"),
                GetBool(obj, "stop", false)),
            null,
            response);
    }

    public static AgenticSynthesisResult ParseSynthesis(string response) =>
        new(CleanAnswer(response));

    public static AgenticVerificationParseResult ParseVerification(
        string response,
        int maxTasks)
    {
        var parsed = ParseJsonResponse(response);
        if (!parsed.Succeeded || parsed.Object is null)
        {
            return new AgenticVerificationParseResult(null, parsed.Error, response);
        }

        var obj = parsed.Object;
        var status = GetString(obj, "status", string.Empty);
        var passed = status.Equals("pass", StringComparison.OrdinalIgnoreCase);
        var issues = obj["issues"] is JsonArray issueArray
            ? issueArray
                .Select(node => node?.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList()
            : [];

        var tasks = new List<AgenticPlanTask>();
        if (!passed && obj["tasks"] is JsonArray taskArray)
        {
            foreach (var taskNode in taskArray)
            {
                if (taskNode is not JsonObject taskObject)
                {
                    continue;
                }

                var id = GetScalarString(taskObject, "id");
                var query = GetScalarString(taskObject, "query");
                var question = GetScalarString(taskObject, "question") ?? query;
                if (!string.IsNullOrWhiteSpace(id)
                    && !string.IsNullOrWhiteSpace(question)
                    && !string.IsNullOrWhiteSpace(query))
                {
                    tasks.Add(new AgenticPlanTask(id, question, query));
                }
            }
        }

        var max = Math.Max(0, maxTasks);
        return new AgenticVerificationParseResult(
            new AgenticVerification(
                passed,
                GetString(obj, "reasoning", string.Empty),
                issues,
                tasks.Take(max).ToList()),
            null,
            response);
    }

    private static bool TryParseObject(string text, out JsonObject? obj)
    {
        obj = null;
        try
        {
            obj = JsonNode.Parse(text) as JsonObject;
            return obj is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetString(JsonObject obj, string key, string fallback) =>
        obj.TryGetPropertyValue(key, out var value) && value is not null
            ? value.GetValue<string>() ?? fallback
            : fallback;

    private static string? GetNullableString(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var value) && value is not null
            ? value.GetValue<string>()
            : null;

    private static string? GetScalarString(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var value) || value is null)
        {
            return null;
        }

        return value is JsonValue ? value.ToString() : null;
    }

    private static bool GetBool(JsonObject obj, string key, bool fallback) =>
        obj.TryGetPropertyValue(key, out var value) && value is not null
            ? value.GetValue<bool>()
            : fallback;

    private static string CleanAnswer(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = BoldRegex().Replace(text, "$1");
        text = ItalicRegex().Replace(text, "$1");
        text = HeadingRegex().Replace(text, string.Empty);

        var lines = text.Split('\n');
        var resultLines = new List<string>();
        var bulletBuffer = new List<string>();

        void FlushBullets()
        {
            if (bulletBuffer.Count == 0)
            {
                return;
            }

            resultLines.Add(string.Join("; ", bulletBuffer) + ".");
            bulletBuffer.Clear();
        }

        foreach (var line in lines)
        {
            var stripped = line.Trim();
            if (BulletRegex().IsMatch(stripped))
            {
                var content = BulletRegex()
                    .Replace(stripped, string.Empty)
                    .TrimEnd('.', ';', ',');
                if (!string.IsNullOrWhiteSpace(content))
                {
                    bulletBuffer.Add(content);
                }
            }
            else
            {
                FlushBullets();
                resultLines.Add(line);
            }
        }

        FlushBullets();
        text = string.Join("\n", resultLines);
        text = ColonSemicolonRegex().Replace(text, ": ");
        text = ExcessNewlinesRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    private static IReadOnlyList<string> ExtractTopLevelObjects(string text)
    {
        var candidates = new List<string>();
        var depth = 0;
        var start = -1;
        var inString = false;
        var escape = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (ch == '\\')
                {
                    escape = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == '{')
            {
                if (depth == 0)
                {
                    start = i;
                }

                depth++;
            }
            else if (ch == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    candidates.Add(text[start..(i + 1)]);
                    start = -1;
                }
            }
        }

        return candidates;
    }

    private static string SanitizeJsonString(string raw)
    {
        raw = MissingColonBeforeContainerRegex().Replace(raw, "\"$1\": $2");
        raw = MissingQuoteColonBeforeContainerRegex().Replace(raw, "\"$1\": $2");

        var output = new System.Text.StringBuilder(raw.Length);
        var inString = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (ch == '\\' && inString)
            {
                output.Append(ch);
                if (i + 1 < raw.Length)
                {
                    i++;
                    output.Append(raw[i]);
                }

                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                output.Append(ch);
                continue;
            }

            if (inString)
            {
                output.Append(ch switch
                {
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => ch
                });
                continue;
            }

            output.Append(ch);
        }

        return output.ToString();
    }

    [GeneratedRegex("\"(\\w+)\"\\s*(\\[|\\{)", RegexOptions.Compiled)]
    private static partial Regex MissingColonBeforeContainerRegex();

    [GeneratedRegex("\"(\\w+)(\\[|\\{)", RegexOptions.Compiled)]
    private static partial Regex MissingQuoteColonBeforeContainerRegex();

    [GeneratedRegex(@"\*\*(.+?)\*\*", RegexOptions.Compiled)]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"(?<!\w)\*([^*\n]+?)\*(?!\w)", RegexOptions.Compiled)]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"^#{1,6}\s+", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(?:[-*+\u2022]\s|\d+[.)]\s)", RegexOptions.Compiled)]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@":\s*;\s*", RegexOptions.Compiled)]
    private static partial Regex ColonSemicolonRegex();

    [GeneratedRegex(@"\n{3,}", RegexOptions.Compiled)]
    private static partial Regex ExcessNewlinesRegex();
}
