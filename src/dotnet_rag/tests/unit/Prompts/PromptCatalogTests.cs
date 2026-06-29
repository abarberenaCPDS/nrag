using DotnetRag.Rag.Services;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Prompts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotnetRag.Tests.Unit.Prompts;

public sealed class PromptCatalogTests
{
    private static readonly string[] PythonYamlKeys =
    [
        "chat_template",
        "rag_template",
        "query_rewriter_prompt",
        "reflection_relevance_check_prompt",
        "reflection_query_rewriter_prompt",
        "reflection_groundedness_check_prompt",
        "reflection_response_regeneration_prompt",
        "document_summary_prompt",
        "shallow_summary_prompt",
        "iterative_summary_prompt",
        "vlm_template",
        "filter_expression_generator_prompt_milvus",
        "filter_expression_generator_prompt_elasticsearch",
        "query_decomposition_multiquery_prompt",
        "query_decompositions_query_rewriter_prompt",
        "query_decomposition_followup_question_prompt",
        "query_decomposition_final_response_prompt",
        "query_decomposition_rag_template",
        "image_captioning_prompt"
    ];

    [Fact]
    public void Load_LoadsAllPythonYamlPromptKeys()
    {
        var catalog = PromptCatalog.Load(FindRepoFile("src/nvidia_rag/rag_server/prompt.yaml"));

        catalog.Keys.Should().Contain(PythonYamlKeys);
        catalog.RagTemplate.System.Should().Contain("Envie");
        catalog.FilterExpressionGeneratorPromptMilvus.System.Should().Contain("Primary Directive");
        catalog.QueryDecompositionMultiqueryPrompt.Human.Should().Contain("Original question: {question}");
    }

    [Fact]
    public void Load_WithoutOverride_LoadsDefaultPromptYaml()
    {
        var catalog = PromptCatalog.Load(null);

        catalog.Keys.Should().Contain(PythonYamlKeys);
        catalog.RagTemplate.System.Should().Contain("Envie");
        catalog.FilterExpressionGeneratorPromptMilvus.System.Should().Contain("Primary Directive");
    }

    [Fact]
    public async Task DotnetPromptYaml_MatchesPythonPromptYaml_IgnoringLineEndings()
    {
        var pythonPrompt = await File.ReadAllTextAsync(FindRepoFile("src/nvidia_rag/rag_server/prompt.yaml"));
        var dotnetPrompt = await File.ReadAllTextAsync(FindRepoFile("src/dotnet_rag/utils/prompt.yaml"));

        NormalizeLineEndings(dotnetPrompt).Should().Be(NormalizeLineEndings(pythonPrompt));
    }

    [Fact]
    public void Load_ExposesAgenticPromptSections()
    {
        var catalog = PromptCatalog.Load(FindRepoFile("src/nvidia_rag/rag_server/prompt.yaml"));

        catalog.Agentic.PlannerPrompt.System.Should().Contain("retrieval planning assistant");
        catalog.Agentic.TaskAnswerPrompt.Human.Should().Contain("{documents}");
        catalog.Agentic.SeedGenerationPrompt.System.Should().Contain("search query specialist");
        catalog.Agentic.SynthesisPrompt.Human.Should().Contain("{sub_answers}");
        catalog.Agentic.VerificationPrompt.System.Should().Contain("quality checker");
        catalog.Agentic.PlannerReplanInstruction.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Load_ExposesAgenticPromptsFromMarkdownDefaults()
    {
        var catalog = PromptCatalog.Load(null);

        catalog.Agentic.PlannerPrompt.System.Should().Contain("{max_plan_tasks}");
        catalog.Agentic.PlannerPrompt.Human.Should().Contain("{query}");
        catalog.Agentic.PlannerPrompt.Human.Should().Contain("{initial_context}");
        catalog.Agentic.TaskAnswerPrompt.Human.Should().Contain("{documents}");
        catalog.Agentic.SeedGenerationPrompt.Human.Should().Contain("{tried_queries}");
        catalog.Agentic.SynthesisPrompt.Human.Should().Contain("{sub_answers}");
        catalog.Agentic.VerificationPrompt.System.Should().Contain("{max_verification_tasks}");
        catalog.Agentic.PlannerReplanInstruction.Should().Contain("scope discovery");
    }

    [Fact]
    public void AgenticMarkdownDefaults_AreCopiedToOutput()
    {
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "Prompts", "Defaults", "agentic");

        Directory.Exists(outputDirectory).Should().BeTrue("Agentic markdown prompt defaults must be copied to the test output");
        File.Exists(Path.Combine(outputDirectory, "planner.system.md")).Should().BeTrue();
        File.Exists(Path.Combine(outputDirectory, "planner.human.md")).Should().BeTrue();
        File.Exists(Path.Combine(outputDirectory, "planner_replan_instruction.md")).Should().BeTrue();
        File.Exists(Path.Combine(outputDirectory, "task_answer.system.md")).Should().BeTrue();
        File.Exists(Path.Combine(outputDirectory, "task_answer.human.md")).Should().BeTrue();
        File.Exists(Path.Combine(outputDirectory, "seed_generation.system.md")).Should().BeTrue();
        File.Exists(Path.Combine(outputDirectory, "seed_generation.human.md")).Should().BeTrue();
        File.Exists(Path.Combine(outputDirectory, "synthesis.system.md")).Should().BeTrue();
        File.Exists(Path.Combine(outputDirectory, "synthesis.human.md")).Should().BeTrue();
        File.Exists(Path.Combine(outputDirectory, "verification.system.md")).Should().BeTrue();
        File.Exists(Path.Combine(outputDirectory, "verification.human.md")).Should().BeTrue();
    }

    [Fact]
    public void Load_UsesCustomPromptFileOverrides()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"prompt-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(temp, """
chat_template:
  system: |
    CUSTOM CHAT SYSTEM
  human: |
    CUSTOM {input}
""");

        try
        {
            var catalog = PromptCatalog.Load(temp);

            catalog.ChatTemplate.System.Should().Be("CUSTOM CHAT SYSTEM");
            PromptCatalog.Render(catalog.ChatTemplate.Human, new Dictionary<string, string?>
            {
                ["input"] = "hello"
            }).Should().Be("CUSTOM hello");
            catalog.RagTemplate.System.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void RagServerConfiguration_RoleEndpointsAndApiKeys_FallBackToMainLlm()
    {
        var previous = Environment.GetEnvironmentVariable("NVIDIA_API_KEY");
        Environment.SetEnvironmentVariable("NVIDIA_API_KEY", "main-key");

        try
        {
            var config = new RagServerConfiguration
            {
                LlmEndpoint = "http://llm:8000",
                QueryRewriterEndpoint = "",
                QueryRewriterApiKey = "",
                FilterExpressionGeneratorEndpoint = "http://filter:8000/v1",
                FilterExpressionGeneratorApiKey = "filter-key",
                ReflectionEndpoint = "",
                ReflectionApiKey = ""
            };

            config.QueryRewriterEndpointOrDefault.Should().Be("http://llm:8000");
            config.QueryRewriterApiKeyOrDefault.Should().Be("main-key");
            config.FilterExpressionGeneratorEndpointOrDefault.Should().Be("http://filter:8000/v1");
            config.FilterExpressionGeneratorApiKeyOrDefault.Should().Be("filter-key");
            config.ReflectionEndpointOrDefault.Should().Be("http://llm:8000");
            config.ReflectionApiKeyOrDefault.Should().Be("main-key");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NVIDIA_API_KEY", previous);
        }
    }

    [Fact]
    public async Task QueryRewritingService_RendersYamlPrompt()
    {
        ChatCompletionRequest? captured = null;
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new ChatCompletionResponse("standalone", null));

        var service = new QueryRewritingService(
            chat.Object,
            new RagServerConfiguration { ConversationHistory = 5, QueryRewriterModel = "rewrite-model" },
            PromptCatalog.Load(FindRepoFile("src/nvidia_rag/rag_server/prompt.yaml")),
            NullLogger<QueryRewritingService>.Instance);

        await service.RewriteAsync("What about revenue?", [new("user", "Tell me about NVIDIA")]);

        captured.Should().NotBeNull();
        captured!.Model.Should().Be("rewrite-model");
        captured.Messages[0].Content.ToString().Should().Contain("Given the following chat history");
        captured.Messages[1].Content.ToString().Should().Contain("Latest Question: What about revenue?");
    }

    [Fact]
    public async Task FilterExpressionService_RendersMilvusYamlPrompt()
    {
        ChatCompletionRequest? captured = null;
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new ChatCompletionResponse("content_metadata[\"year\"] == 2024", null));

        var capabilities = new Mock<IVectorStoreFilterCapabilities>();
        capabilities.SetupGet(c => c.SupportsGeneratedFilters).Returns(true);
        capabilities.SetupGet(c => c.GeneratedFilterPromptKind).Returns(GeneratedFilterPromptKind.Milvus);
        capabilities.Setup(c => c.GetFilterSchemaDescriptionAsync("col", It.IsAny<CancellationToken>()))
            .ReturnsAsync("year: integer");

        var service = new FilterExpressionService(
            chat.Object,
            capabilities.Object,
            new RagServerConfiguration
            {
                EnableFilterGenerator = true,
                VectorStoreName = "milvus",
                FilterExpressionGeneratorModel = "filter-model",
                FilterExpressionGeneratorTemperature = 0.2,
                FilterExpressionGeneratorTopP = 0.7,
                FilterExpressionGeneratorMaxTokens = 123
            },
            PromptCatalog.Load(FindRepoFile("src/nvidia_rag/rag_server/prompt.yaml")),
            NullLogger<FilterExpressionService>.Instance);

        var result = await service.GenerateAsync("2024 reports", "col");

        result.Should().Be("content_metadata[\"year\"] == 2024");
        captured.Should().NotBeNull();
        captured!.Model.Should().Be("filter-model");
        captured.Temperature.Should().Be(0.2);
        captured.TopP.Should().Be(0.7);
        captured.MaxTokens.Should().Be(123);
        captured.Messages[0].Content.ToString().Should().Contain("Primary Directive");
        captured.Messages[0].Content.ToString().Should().Contain("year: integer");
        captured.Messages[0].Content.ToString().Should().Contain("2024 reports");
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }

    private static string NormalizeLineEndings(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }
}
