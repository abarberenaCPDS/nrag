using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DotnetRag.Shared.Summarization;

// ORIG: nvidia_rag/rag_server/
// — document_summary_prompt, shallow_summary_prompt, iterative_summary_prompt
// Loads prompts from prompt.yaml; falls back to built-in defaults if the file is absent.
public sealed class SummarizationPrompts
{
    public string DocumentSummarySystem { get; private init; } = DefaultDocumentSystem;
    public string DocumentSummaryHuman { get; private init; } = DefaultHuman;
    public string ShallowSummarySystem { get; private init; } = DefaultShallowSystem;
    public string ShallowSummaryHuman { get; private init; } = DefaultHuman;
    public string IterativeSummarySystem { get; private init; } = DefaultIterativeSystem;
    public string IterativeSummaryHuman { get; private init; } = DefaultIterativeHuman;

    // ── Defaults (mirrors prompt.yaml structure) ──────────────────────────────

    private const string DefaultDocumentSystem =
        "You are an expert document summarizer. Produce a single, self-contained paragraph " +
        "of exactly 5–6 sentences that captures the document's essential meaning. Be concise and accurate.";

    private const string DefaultShallowSystem =
        "Please provide a concise summary for the following document:";

    private const string DefaultIterativeSystem =
        "You are maintaining a running summary of a long document as new sections arrive. " +
        "Given a PREVIOUS SUMMARY and a NEW CHUNK, produce an UPDATED SUMMARY. " +
        "Retain the most important findings, incorporate significant new content, and keep the " +
        "summary to 10 sentences maximum. SYNTHESIZE — do not enumerate.";

    private const string DefaultHuman = "{document_text}";

    private const string DefaultIterativeHuman =
        "Previous Summary:\n{previous_summary}\n\nNew chunk:\n{new_chunk}\n\n" +
        "Updated summary (10 sentences maximum):";

    // ── Factory ───────────────────────────────────────────────────────────────

    public static SummarizationPrompts Load(string? promptConfigFile)
    {
        if (string.IsNullOrWhiteSpace(promptConfigFile) || !File.Exists(promptConfigFile))
        {
            return new SummarizationPrompts();
        }

        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            using var reader = new StreamReader(promptConfigFile);
            var yaml = deserializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(reader);

            static string Get(Dictionary<string, Dictionary<string, string>> map, string key, string field, string fallback)
                => map.TryGetValue(key, out var section) && section.TryGetValue(field, out var val)
                    ? val.TrimEnd()
                    : fallback;

            return new SummarizationPrompts
            {
                DocumentSummarySystem = Get(yaml, "document_summary_prompt", "system", DefaultDocumentSystem),
                DocumentSummaryHuman = Get(yaml, "document_summary_prompt", "human", DefaultHuman),
                ShallowSummarySystem = Get(yaml, "shallow_summary_prompt", "system", DefaultShallowSystem),
                ShallowSummaryHuman = Get(yaml, "shallow_summary_prompt", "human", DefaultHuman),
                IterativeSummarySystem = Get(yaml, "iterative_summary_prompt", "system", DefaultIterativeSystem),
                IterativeSummaryHuman = Get(yaml, "iterative_summary_prompt", "human", DefaultIterativeHuman),
            };
        }
        catch
        {
            // Malformed YAML → fall back to defaults
            return new SummarizationPrompts();
        }
    }
}
