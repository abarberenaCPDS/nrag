using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DotnetRag.Shared.Prompts;

public sealed record PromptSection(string System, string Human);

public sealed record AgenticPromptCatalog(
    PromptSection PlannerPrompt,
    PromptSection TaskAnswerPrompt,
    PromptSection SeedGenerationPrompt,
    PromptSection SynthesisPrompt,
    PromptSection VerificationPrompt,
    string PlannerReplanInstruction);

public sealed class PromptCatalog
{
    private readonly Dictionary<string, PromptSection> sections;

    private PromptCatalog(Dictionary<string, PromptSection> sections)
    {
        this.sections = sections;
        Agentic = BuildAgenticPrompts();
    }

    public PromptSection ChatTemplate => Get("chat_template");
    public PromptSection RagTemplate => Get("rag_template");
    public PromptSection VlmTemplate => Get("vlm_template");
    public PromptSection QueryRewriterPrompt => Get("query_rewriter_prompt");
    public PromptSection ReflectionRelevanceCheckPrompt => Get("reflection_relevance_check_prompt");
    public PromptSection ReflectionQueryRewriterPrompt => Get("reflection_query_rewriter_prompt");
    public PromptSection ReflectionGroundednessCheckPrompt => Get("reflection_groundedness_check_prompt");
    public PromptSection ReflectionResponseRegenerationPrompt => Get("reflection_response_regeneration_prompt");
    public PromptSection DocumentSummaryPrompt => Get("document_summary_prompt");
    public PromptSection ShallowSummaryPrompt => Get("shallow_summary_prompt");
    public PromptSection IterativeSummaryPrompt => Get("iterative_summary_prompt");
    public PromptSection FilterExpressionGeneratorPromptMilvus => Get("filter_expression_generator_prompt_milvus");
    public PromptSection FilterExpressionGeneratorPromptElasticsearch => Get("filter_expression_generator_prompt_elasticsearch");
    public PromptSection QueryDecompositionMultiqueryPrompt => Get("query_decomposition_multiquery_prompt");
    public PromptSection QueryDecompositionQueryRewriterPrompt => Get("query_decompositions_query_rewriter_prompt");
    public PromptSection QueryDecompositionFollowupQuestionPrompt => Get("query_decomposition_followup_question_prompt");
    public PromptSection QueryDecompositionFinalResponsePrompt => Get("query_decomposition_final_response_prompt");
    public PromptSection QueryDecompositionRagTemplate => Get("query_decomposition_rag_template");
    public PromptSection ImageCaptioningPrompt => Get("image_captioning_prompt");
    public AgenticPromptCatalog Agentic { get; }

    public IReadOnlyCollection<string> Keys => sections.Keys;

    public static PromptCatalog Load(string? promptConfigFile)
    {
        var sections = LoadDefaultSections();
        var overridePath = ResolvePromptPath(promptConfigFile);
        if (overridePath is not null)
        {
            ApplySections(sections, LoadSectionsFromYaml(overridePath));
        }

        return new PromptCatalog(sections);
    }

    private static Dictionary<string, PromptSection> LoadDefaultSections()
    {
        var defaultPath = ResolveDefaultPromptPath();
        var sections = new Dictionary<string, PromptSection>(StringComparer.Ordinal);
        ApplySections(sections, LoadSectionsFromYaml(defaultPath));
        return sections;
    }

    private static Dictionary<string, Dictionary<string, string>> LoadSectionsFromYaml(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        using var reader = new StreamReader(path);
        return deserializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(reader)
               ?? [];
    }

    private static void ApplySections(
        Dictionary<string, PromptSection> sections,
        Dictionary<string, Dictionary<string, string>> yaml)
    {
        foreach (var (key, value) in yaml)
        {
            sections[key] = new PromptSection(
                Normalize(value.GetValueOrDefault("system")),
                Normalize(value.GetValueOrDefault("human")));
        }
    }

    public PromptSection Get(string key) =>
        sections.TryGetValue(key, out var section)
            ? section
            : throw new KeyNotFoundException($"Prompt section '{key}' is not loaded.");

    public static string Render(string template, IReadOnlyDictionary<string, string?> values)
    {
        var rendered = template;
        foreach (var (key, value) in values)
        {
            rendered = rendered.Replace("{" + key + "}", value ?? string.Empty, StringComparison.Ordinal);
        }

        return rendered.TrimEnd();
    }

    private static string? ResolvePromptPath(string? promptConfigFile)
    {
        if (!string.IsNullOrWhiteSpace(promptConfigFile) && File.Exists(promptConfigFile))
        {
            return promptConfigFile;
        }

        return null;
    }

    private static string ResolveDefaultPromptPath()
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "prompt.yaml");
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var sourcePath = Path.Combine(
                dir.FullName,
                "src",
                "dotnet_rag",
                "utils",
                "prompt.yaml");
            if (File.Exists(sourcePath))
            {
                return sourcePath;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find default prompt.yaml at '{outputPath}' or under the repository source tree.",
            outputPath);
    }

    private static string Normalize(string? value) => (value ?? string.Empty).TrimEnd();

    private static string LoadPromptFile(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required Agentic prompt file '{path}' was not found.", path);
        }

        return File.ReadAllText(path).TrimEnd();
    }

    private static PromptSection LoadPromptSection(string directory, string name) => new(
        LoadPromptFile(directory, $"{name}.system.md"),
        LoadPromptFile(directory, $"{name}.human.md"));

    private static string ResolveAgenticPromptDirectory()
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "Prompts", "Defaults", "agentic");
        if (Directory.Exists(outputPath))
        {
            return outputPath;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var sourcePath = Path.Combine(
                dir.FullName,
                "src",
                "dotnet_rag",
                "utils",
                "Prompts",
                "Defaults",
                "agentic");
            if (Directory.Exists(sourcePath))
            {
                return sourcePath;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find Agentic prompt directory at '{outputPath}' or under the repository source tree.");
    }

    private static AgenticPromptCatalog BuildAgenticPrompts()
    {
        var directory = ResolveAgenticPromptDirectory();
        return new AgenticPromptCatalog(
            PlannerPrompt: LoadPromptSection(directory, "planner"),
            TaskAnswerPrompt: LoadPromptSection(directory, "task_answer"),
            SeedGenerationPrompt: LoadPromptSection(directory, "seed_generation"),
            SynthesisPrompt: LoadPromptSection(directory, "synthesis"),
            VerificationPrompt: LoadPromptSection(directory, "verification"),
            PlannerReplanInstruction: LoadPromptFile(directory, "planner_replan_instruction.md"));
    }
}
