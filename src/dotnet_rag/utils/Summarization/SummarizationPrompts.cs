using DotnetRag.Shared.Prompts;

namespace DotnetRag.Shared.Summarization;

// ORIG: nvidia_rag/rag_server/
// — document_summary_prompt, shallow_summary_prompt, iterative_summary_prompt
// Thin summarization view over the shared prompt catalog.
public sealed class SummarizationPrompts
{
    public string DocumentSummarySystem { get; private init; } = "";
    public string DocumentSummaryHuman { get; private init; } = "";
    public string ShallowSummarySystem { get; private init; } = "";
    public string ShallowSummaryHuman { get; private init; } = "";
    public string IterativeSummarySystem { get; private init; } = "";
    public string IterativeSummaryHuman { get; private init; } = "";

    // ── Factory ───────────────────────────────────────────────────────────────

    public static SummarizationPrompts Load(string? promptConfigFile)
        => FromCatalog(PromptCatalog.Load(promptConfigFile));

    public static SummarizationPrompts FromCatalog(PromptCatalog catalog)
    {
        var document = catalog.DocumentSummaryPrompt;
        var shallow = catalog.ShallowSummaryPrompt;
        var iterative = catalog.IterativeSummaryPrompt;

        return new SummarizationPrompts
        {
            DocumentSummarySystem = document.System,
            DocumentSummaryHuman = document.Human,
            ShallowSummarySystem = shallow.System,
            ShallowSummaryHuman = shallow.Human,
            IterativeSummarySystem = iterative.System,
            IterativeSummaryHuman = iterative.Human
        };
    }
}
