using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics.Metrics;
using DotnetRag.Rag.Observability;
using DotnetRag.Rag.Services;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using DotnetRag.Shared.Models;
using DotnetRag.Shared.Prompts;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DotnetRag.Tests.Unit.Services;

public sealed class AgenticRagServiceTests
{
    [Fact]
    public void AgenticRagInvocation_FromPrompt_CapturesStableRuntimeInputs()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/generate";
        var prompt = new Prompt(
            [
                new Message("system", "ignored"),
                new Message("user", "What changed?")
            ],
            CollectionNames: ["docs", "tickets"],
            Agentic: true);

        var invocation = AgenticRagInvocation.From(context.Request, prompt);

        invocation.Prompt.Should().BeSameAs(prompt);
        invocation.RequestPath.Should().Be("/generate");
        invocation.IsStreaming.Should().BeTrue();
        invocation.UserQuery.Should().Be("What changed?");
        invocation.CollectionNames.Should().Equal("docs", "tickets");
    }

    [Fact]
    public void AgenticRagInvocation_FromChatCompletionsPath_MarksNonStreaming()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/chat/completions";
        var prompt = new Prompt([new Message("user", "Hello")], Agentic: true);

        var invocation = AgenticRagInvocation.From(context.Request, prompt);

        invocation.IsStreaming.Should().BeFalse();
    }

    [Fact]
    public async Task FeatureFlaggedAgenticRagService_WhenDisabled_ReturnsUnavailable()
    {
        var orchestration = new Mock<IAgenticOrchestrationService>();
        var service = new FeatureFlaggedAgenticRagService(
            new RagServerConfiguration { EnableAgenticRag = false },
            BuildAgenticServiceProvider(orchestration.Object),
            NullLogger<FeatureFlaggedAgenticRagService>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/chat/completions";
        var prompt = new Prompt([new Message("user", "Hello")], Agentic: true);

        var result = await service.GenerateAsync(AgenticRagInvocation.From(context.Request, prompt));

        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        orchestration.Verify(
            o => o.RunOneTaskAsync(
                It.IsAny<AgenticOrchestrationRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<AgenticOrchestrationEvent>?>(),
                It.IsAny<Action<ChatStreamDelta>?>()),
            Times.Never);
    }

    [Fact]
    public async Task FeatureFlaggedAgenticRagService_WhenStreamingEnabled_ReturnsSseResponseWithCitations()
    {
        AgenticOrchestrationRequest? captured = null;
        var orchestration = new Mock<IAgenticOrchestrationService>();
        orchestration.Setup(o => o.RunOneTaskAsync(
                It.IsAny<AgenticOrchestrationRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<AgenticOrchestrationEvent>?>(),
                It.IsAny<Action<ChatStreamDelta>?>()))
            .Callback<AgenticOrchestrationRequest, CancellationToken, Action<AgenticOrchestrationEvent>?, Action<ChatStreamDelta>?>(
                (request, _, onEvent, onAnswerDelta) =>
                {
                    captured = request;
                    onEvent?.Invoke(new AgenticOrchestrationEvent(
                        "stage_start",
                        "plan",
                        "Planning the next retrieval steps..."));
                    onEvent?.Invoke(new AgenticOrchestrationEvent(
                        "stage_end",
                        "plan",
                        "Created 1 targeted retrieval task(s)."));
                    onEvent?.Invoke(new AgenticOrchestrationEvent(
                        "stage_start",
                        "execute",
                        "Executing 1 retrieval task(s)."));
                    onEvent?.Invoke(new AgenticOrchestrationEvent(
                        "stage_end",
                        "execute",
                        "Completed retrieval (1 task(s) answered)."));
                    onEvent?.Invoke(new AgenticOrchestrationEvent(
                        "stage_start",
                        "synthesize",
                        "Composing the answer..."));
                    onAnswerDelta?.Invoke(new ChatStreamDelta(Content: "Revenue was $10M."));
                    onEvent?.Invoke(new AgenticOrchestrationEvent(
                        "stage_end",
                        "synthesize",
                        "Answer ready."));
                    onEvent?.Invoke(new AgenticOrchestrationEvent(
                        "stage_start",
                        "verify",
                        "Reviewing the answer for completeness..."));
                    onEvent?.Invoke(new AgenticOrchestrationEvent(
                        "stage_end",
                        "verify",
                        "Answer looks complete."));
                })
            .ReturnsAsync(new AgenticOrchestrationResult(
                true,
                "Revenue was $10M.",
                null,
                [],
                [
                    new AgenticCitation(
                        "doc-1",
                        "Revenue was $10M.",
                        0.95,
                        "t1",
                        new Dictionary<string, string>
                        {
                            ["filename"] = "report.pdf"
                        })
                ],
                new AgenticVerification(true, "complete", [], [])));
        var service = new FeatureFlaggedAgenticRagService(
            new RagServerConfiguration { EnableAgenticRag = true },
            BuildAgenticServiceProvider(orchestration.Object),
            NullLogger<FeatureFlaggedAgenticRagService>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/generate";
        context.Response.Body = new MemoryStream();
        var prompt = new Prompt(
            [new Message("user", "What was revenue?")],
            CollectionNames: ["docs"],
            Agentic: true);

        var result = await service.GenerateAsync(AgenticRagInvocation.From(context.Request, prompt));
        await result.ExecuteAsync(context);

        captured.Should().NotBeNull();
        captured!.Query.Should().Be("What was revenue?");
        captured.CollectionNames.Should().Equal("docs");
        context.Response.ContentType.Should().Be("text/event-stream; charset=utf-8");

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        var events = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        events.Should().HaveCountGreaterThan(2);
        body.Should().Contain("\"event_type\":\"stage_start\"");
        body.Should().Contain("\"event_type\":\"stage_end\"");
        body.Should().Contain("\"stage\":\"plan\"");
        body.Should().Contain("\"stage\":\"execute\"");
        body.Should().Contain("\"stage\":\"synthesize\"");
        body.Should().Contain("\"stage\":\"verify\"");

        var answerEvent = events.First(item => item.Contains("\"content\":\"Revenue was $10M.\""));
        using var deltaJson = JsonDocument.Parse(answerEvent["data: ".Length..]);
        deltaJson.RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString()
            .Should().Be("Revenue was $10M.");

        using var finalJson = JsonDocument.Parse(events[^1]["data: ".Length..]);
        finalJson.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString().Should().Be("stop");
        finalJson.RootElement.GetProperty("citations").GetProperty("results")[0].GetProperty("document_id").GetString()
            .Should().Be("doc-1");
    }

    [Fact]
    public async Task FeatureFlaggedAgenticRagService_WhenEnabledNonStreaming_ReturnsOpenAiResponseWithCitations()
    {
        AgenticOrchestrationRequest? captured = null;
        var orchestration = new Mock<IAgenticOrchestrationService>();
        orchestration.Setup(o => o.RunOneTaskAsync(
                It.IsAny<AgenticOrchestrationRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<AgenticOrchestrationEvent>?>(),
                It.IsAny<Action<ChatStreamDelta>?>()))
            .Callback<AgenticOrchestrationRequest, CancellationToken, Action<AgenticOrchestrationEvent>?, Action<ChatStreamDelta>?>(
                (request, _, _, _) => captured = request)
            .ReturnsAsync(new AgenticOrchestrationResult(
                true,
                "Revenue was $10M.",
                null,
                [],
                [
                    new AgenticCitation(
                        "doc-1",
                        "Revenue was $10M.",
                        0.95,
                        "t1",
                        new Dictionary<string, string>
                        {
                            ["filename"] = "report.pdf",
                            ["collection_name"] = "docs"
                        })
                ]));
        var service = new FeatureFlaggedAgenticRagService(
            new RagServerConfiguration { EnableAgenticRag = true, LlmModel = "agentic-model" },
            BuildAgenticServiceProvider(orchestration.Object),
            NullLogger<FeatureFlaggedAgenticRagService>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/chat/completions";
        var prompt = new Prompt(
            [new Message("user", "What was revenue?")],
            CollectionNames: ["docs"],
            Agentic: true);

        var result = await service.GenerateAsync(AgenticRagInvocation.From(context.Request, prompt));

        result.Should().BeAssignableTo<IValueHttpResult>();
        captured.Should().NotBeNull();
        captured!.Query.Should().Be("What was revenue?");
        captured.CollectionNames.Should().Equal("docs");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(((IValueHttpResult)result).Value));
        json.RootElement.GetProperty("model").GetString().Should().Be("agentic-model");
        json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            .Should().Be("Revenue was $10M.");
        var citation = json.RootElement.GetProperty("citations").GetProperty("results")[0];
        citation.GetProperty("document_id").GetString().Should().Be("doc-1");
        citation.GetProperty("metadata").GetProperty("agentic_task_id").GetString().Should().Be("t1");
    }

    [Fact]
    public async Task FeatureFlaggedAgenticRagService_WithProviderOverrides_UsesRequestScopedFactories()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/chat/completions";
        context.Request.Headers.Authorization = "Bearer request-token";
        var prompt = new Prompt(
            [new Message("user", "What is covered?")],
            CollectionNames: ["docs"],
            VdbEndpoint: "http://request-vdb:8000",
            LlmEndpoint: "http://request-llm:8000/v1",
            EmbeddingEndpoint: "http://request-embed:8000/v1",
            EmbeddingModel: "request-embedding",
            Model: "request-model",
            Agentic: true);

        var chatRequests = new List<ChatCompletionRequest>();
        var chatCalls = 0;
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatCompletionRequest request, CancellationToken _) =>
            {
                chatRequests.Add(request);
                return chatCalls++ switch
                {
                    0 => new ChatCompletionResponse("""
                    {
                      "scope_only": false,
                      "resolved_query": "What is covered?",
                      "tasks": [
                        { "id": "t1", "question": "What is covered?", "query": "coverage" }
                      ]
                    }
                    """),
                    1 => new ChatCompletionResponse("""
                    { "completeness": "complete", "answer": "Coverage includes Agentic RAG.", "missing": "" }
                    """),
                    2 => new ChatCompletionResponse("Coverage includes Agentic RAG."),
                    _ => new ChatCompletionResponse("""
                    { "passed": true, "reasoning": "complete", "issues": [], "tasks": [] }
                    """)
                };
            });
        var chatFactory = new Mock<IChatCompletionClientFactory>();
        chatFactory.Setup(f => f.Create(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>()))
            .Returns(chat.Object);

        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(v => v.SearchAsync("docs", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string query, int _, CancellationToken _) =>
                query == "coverage"
                    ? [new VectorSearchResult("doc-1", "Coverage includes Agentic RAG.", 0.91)]
                    : Array.Empty<VectorSearchResult>());
        var vectorFactory = new Mock<IVectorStoreClientFactory>();
        vectorFactory.Setup(f => f.Create(
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .Returns(new VectorStoreClient(
                vectorStore.Object,
                Mock.Of<IVectorStoreManagement>()));

        using var metrics = new RagMetrics();
        var services = new ServiceCollection()
            .AddSingleton(new RagServerConfiguration
            {
                EnableAgenticRag = true,
                LlmModel = "configured-model",
                LlmProvider = "openai"
            })
            .AddSingleton(PromptCatalog.Load(null))
            .AddSingleton(metrics)
            .AddSingleton(Mock.Of<IVectorStore>())
            .AddSingleton(vectorFactory.Object)
            .AddSingleton(chatFactory.Object)
            .AddSingleton(Mock.Of<IAgenticOrchestrationService>())
            .AddLogging()
            .BuildServiceProvider();
        var service = new FeatureFlaggedAgenticRagService(
            services.GetRequiredService<RagServerConfiguration>(),
            services,
            NullLogger<FeatureFlaggedAgenticRagService>.Instance);

        var result = await service.GenerateAsync(AgenticRagInvocation.From(context.Request, prompt));

        result.Should().NotBeNull();
        vectorFactory.Verify(f => f.Create(
            "http://request-vdb:8000",
            "request-token",
            "http://request-embed:8000/v1",
            "request-embedding"), Times.Once);
        chatFactory.Verify(f => f.Create(
            "openai",
            "request-model",
            "http://request-llm:8000/v1",
            It.IsAny<string?>()), Times.Once);
        chatRequests.Should().HaveCount(4);
        chatRequests.Should().OnlyContain(request => request.Model == "request-model");
    }

    [Fact]
    public void AgenticResponseParser_ParseJsonResponse_ParsesDirectJsonObject()
    {
        var result = AgenticResponseParser.ParseJsonResponse("""
        {"scope_only": false, "tasks": []}
        """);

        result.Succeeded.Should().BeTrue();
        result.Object!["scope_only"]!.GetValue<bool>().Should().BeFalse();
        result.Object!["tasks"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void AgenticResponseParser_ParseJsonResponse_UsesLastBalancedObject()
    {
        var result = AgenticResponseParser.ParseJsonResponse("""
        draft: {"scope_only": true, "tasks": []}
        revised: {"scope_only": false, "tasks": [{"id":"t1","question":"q","query":"search q"}]}
        """);

        result.Succeeded.Should().BeTrue();
        result.Object!["scope_only"]!.GetValue<bool>().Should().BeFalse();
        result.Object!["tasks"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public void AgenticResponseParser_ParseJsonResponse_RepairsMissingColonBeforeArray()
    {
        var result = AgenticResponseParser.ParseJsonResponse("""
        {"scope_only": false, "tasks[{"id":"t1"}]}
        """);

        result.Succeeded.Should().BeTrue();
        result.Object!["tasks"]!.AsArray()[0]!["id"]!.GetValue<string>().Should().Be("t1");
    }

    [Fact]
    public void AgenticResponseParser_ParseJsonResponse_ReturnsErrorWhenNoObjectExists()
    {
        var result = AgenticResponseParser.ParseJsonResponse("not json");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("Failed to parse JSON");
        result.RawResponse.Should().Be("not json");
    }

    [Fact]
    public void AgenticResponseParser_ParsePlan_ReturnsTypedPlannerOutput()
    {
        var result = AgenticResponseParser.ParsePlan("""
        {
          "scope_only": false,
          "scope_resolution": "Compare revenue and margin.",
          "resolved_query": "How did revenue and margin compare in 2024?",
          "tasks": [
            {
              "id": "t1",
              "question": "What was revenue in 2024?",
              "query": "2024 revenue"
            }
          ],
          "synthesis_instruction": "Compare the values."
        }
        """);

        result.Succeeded.Should().BeTrue();
        result.Plan!.ScopeOnly.Should().BeFalse();
        result.Plan.ScopeResolution.Should().Be("Compare revenue and margin.");
        result.Plan.ResolvedQuery.Should().Be("How did revenue and margin compare in 2024?");
        result.Plan.Tasks.Should().ContainSingle().Which.Should().Be(
            new AgenticPlanTask("t1", "What was revenue in 2024?", "2024 revenue"));
        result.Plan.SynthesisInstruction.Should().Be("Compare the values.");
    }

    [Fact]
    public void AgenticResponseParser_ParsePlan_NormalizesNumericIdAndUsesQueryAsQuestionFallback()
    {
        var result = AgenticResponseParser.ParsePlan("""
        {
          "scope_only": false,
          "tasks": [
            {
              "id": 1,
              "action": "retrieve",
              "query": "who is the assistant?"
            }
          ]
        }
        """);

        result.Succeeded.Should().BeTrue();
        result.Plan!.Tasks.Should().ContainSingle().Which.Should().Be(
            new AgenticPlanTask("1", "who is the assistant?", "who is the assistant?"));
    }

    [Fact]
    public void AgenticResponseParser_ParsePlan_RejectsTaskWithoutRequiredFields()
    {
        var result = AgenticResponseParser.ParsePlan("""
        {
          "scope_only": false,
          "tasks": [
            {
              "id": "t1",
              "question": "What was revenue in 2024?"
            }
          ]
        }
        """);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("Task entries must include id, question, and query");
    }

    [Fact]
    public void AgenticResponseParser_ParseTaskAnswer_ParsesStructuredAnswer()
    {
        var result = AgenticResponseParser.ParseTaskAnswer("""
        {
          "completeness": "partial",
          "answer": "**Revenue** was available.",
          "missing": "Margin"
        }
        """);

        result.Completeness.Should().Be("partial");
        result.Answer.Should().Be("Revenue was available.");
        result.Missing.Should().Be("Margin");
    }

    [Fact]
    public void AgenticResponseParser_ParseTaskAnswer_FallsBackToCleanPlainText()
    {
        var result = AgenticResponseParser.ParseTaskAnswer("""
        # Summary
        - Revenue grew
        - Margin fell
        """);

        result.Completeness.Should().Be("complete");
        result.Answer.Should().Be("Summary\nRevenue grew; Margin fell.");
        result.Missing.Should().BeEmpty();
    }

    [Fact]
    public void AgenticResponseParser_ParseSeedQuery_ReturnsTypedSeedDecision()
    {
        var result = AgenticResponseParser.ParseSeedQuery("""
        {
          "reasoning": "Try alternate terminology.",
          "seed_query": "annual recurring revenue",
          "stop": false
        }
        """);

        result.Succeeded.Should().BeTrue();
        result.SeedQuery!.Reasoning.Should().Be("Try alternate terminology.");
        result.SeedQuery.SeedQuery.Should().Be("annual recurring revenue");
        result.SeedQuery.Stop.Should().BeFalse();
    }

    [Fact]
    public void AgenticResponseParser_ParseSynthesis_CleansFinalAnswer()
    {
        var result = AgenticResponseParser.ParseSynthesis("""
        **Revenue** was $10M.
        """);

        result.Answer.Should().Be("Revenue was $10M.");
    }

    [Fact]
    public void AgenticResponseParser_ParseVerification_ReturnsPass()
    {
        var result = AgenticResponseParser.ParseVerification("""
        {
          "status": "pass",
          "reasoning": "All parts are addressed."
        }
        """, maxTasks: 3);

        result.Succeeded.Should().BeTrue();
        result.Verification!.Passed.Should().BeTrue();
        result.Verification.Reasoning.Should().Be("All parts are addressed.");
        result.Verification.Issues.Should().BeEmpty();
        result.Verification.Tasks.Should().BeEmpty();
    }

    [Fact]
    public void AgenticResponseParser_ParseVerification_FiltersAndTrimsFollowupTasks()
    {
        var result = AgenticResponseParser.ParseVerification("""
        {
          "status": "fail",
          "issues": ["Margin omitted"],
          "tasks": [
            { "id": "v1", "question": "What was margin?", "query": "2024 margin" },
            { "id": "bad", "question": "Missing query" },
            { "id": "v2", "question": "What was revenue?", "query": "2024 revenue" }
          ]
        }
        """, maxTasks: 1);

        result.Succeeded.Should().BeTrue();
        result.Verification!.Passed.Should().BeFalse();
        result.Verification.Issues.Should().Equal("Margin omitted");
        result.Verification.Tasks.Should().ContainSingle().Which.Should().Be(
            new AgenticPlanTask("v1", "What was margin?", "2024 margin"));
    }

    [Fact]
    public void AgenticResponseParser_ParseVerification_NormalizesNumericTaskIds()
    {
        var result = AgenticResponseParser.ParseVerification("""
        {
          "status": "fail",
          "tasks": [
            { "id": 1, "query": "missing assistant identity" }
          ]
        }
        """, maxTasks: 3);

        result.Succeeded.Should().BeTrue();
        result.Verification!.Tasks.Should().ContainSingle().Which.Should().Be(
            new AgenticPlanTask("1", "missing assistant identity", "missing assistant identity"));
    }

    [Fact]
    public async Task AgenticPlannerService_CreatePlanAsync_RendersPromptAndParsesPlan()
    {
        ChatCompletionRequest? captured = null;
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new ChatCompletionResponse("""
            {
              "scope_only": false,
              "scope_resolution": "Revenue is requested.",
              "resolved_query": "What was revenue in 2024?",
              "tasks": [
                {
                  "id": "t1",
                  "question": "What was revenue in 2024?",
                  "query": "2024 revenue"
                }
              ],
              "synthesis_instruction": "Answer with the revenue value."
            }
            """, Usage: new Dictionary<string, object?>
            {
                ["prompt_tokens"] = 10,
                ["completion_tokens"] = 20,
                ["total_tokens"] = 30
            }));
        var service = BuildPlanner(chat.Object, maxTasks: 5);

        var result = await service.CreatePlanAsync(new AgenticPlannerRequest(
            "What was revenue in 2024?",
            "Initial context chunk."));

        result.Succeeded.Should().BeTrue();
        result.Plan!.Tasks.Should().ContainSingle().Which.Query.Should().Be("2024 revenue");
        captured.Should().NotBeNull();
        captured!.Model.Should().Be("planner-model");
        captured.Temperature.Should().Be(0.0);
        captured.TopP.Should().Be(0.1);
        captured.MaxTokens.Should().Be(1024);
        captured.Messages[0].Content.ToString().Should().Contain("Maximum 5 tasks");
        captured.Messages[1].Content.ToString().Should().Contain("What was revenue in 2024?");
        captured.Messages[1].Content.ToString().Should().Contain("Initial context chunk.");
    }

    [Fact]
    public async Task AgenticPlannerService_CreatePlanAsync_RecordsPythonCompatibleLlmMetrics()
    {
        var measurements = new List<(string Name, double Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "dotnet-rag-server"
                && instrument.Name.StartsWith("agentic_llm_", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            measurements.Add((instrument.Name, measurement)));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
            measurements.Add((instrument.Name, measurement)));
        listener.Start();

        using var metrics = new RagMetrics();
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse("""
            {
              "scope_only": false,
              "tasks": [
                { "id": "t1", "question": "Q1", "query": "query one" }
              ]
            }
            """, Usage: new Dictionary<string, object?>
            {
                ["prompt_tokens"] = 13,
                ["completion_tokens"] = 5
            }));
        var service = BuildPlanner(chat.Object, metrics: metrics);

        var result = await service.CreatePlanAsync(new AgenticPlannerRequest("q", "ctx"));

        result.Succeeded.Should().BeTrue();
        measurements.Should().Contain(item => item.Name == "agentic_llm_calls_total" && item.Value == 1);
        measurements.Should().Contain(item => item.Name == "agentic_llm_call_duration_ms" && item.Value >= 0);
        measurements.Should().Contain(item => item.Name == "agentic_llm_tokens_total" && item.Value == 13);
        measurements.Should().Contain(item => item.Name == "agentic_llm_tokens_total" && item.Value == 5);
    }

    [Fact]
    public async Task AgenticPlannerService_CreatePlanAsync_RetriesMalformedPlannerOutput()
    {
        var chat = new Mock<IChatCompletionService>();
        var calls = 0;
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => calls++ == 0
                ? new ChatCompletionResponse("not json")
                : new ChatCompletionResponse("""
                {
                  "scope_only": true,
                  "scope_resolution": "Need discovery.",
                  "resolved_query": "What documents discuss revenue?",
                  "tasks": [
                    {
                      "id": "disc1",
                      "question": "Discover revenue coverage.",
                      "query": "revenue coverage documents"
                    }
                  ],
                  "synthesis_instruction": ""
                }
                """));
        var service = BuildPlanner(chat.Object, maxAttempts: 2);

        var result = await service.CreatePlanAsync(new AgenticPlannerRequest(
            "What documents discuss revenue?",
            ""));

        result.Succeeded.Should().BeTrue();
        result.Plan!.ScopeOnly.Should().BeTrue();
        chat.Verify(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AgenticPlannerService_CreatePlanAsync_TrimsTasksToConfiguredMaximum()
    {
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse("""
            {
              "scope_only": false,
              "tasks": [
                { "id": "t1", "question": "Q1", "query": "query one" },
                { "id": "t2", "question": "Q2", "query": "query two" }
              ]
            }
            """));
        var service = BuildPlanner(chat.Object, maxTasks: 1);

        var result = await service.CreatePlanAsync(new AgenticPlannerRequest("q", "ctx"));

        result.Succeeded.Should().BeTrue();
        result.Plan!.Tasks.Should().ContainSingle().Which.Id.Should().Be("t1");
    }

    [Fact]
    public async Task AgenticRoleService_AnswerTaskAsync_RendersTaskPromptAndParsesAnswer()
    {
        ChatCompletionRequest? captured = null;
        var chat = MockRoleChat("""
        { "completeness": "complete", "answer": "Revenue was $10M.", "missing": "" }
        """, request => captured = request);
        var service = BuildRoles(chat.Object);

        var result = await service.AnswerTaskAsync(new AgenticTaskAnswerRequest(
            "What was revenue?",
            "Revenue was $10M."));

        result.Completeness.Should().Be("complete");
        result.Answer.Should().Be("Revenue was $10M.");
        captured.Should().NotBeNull();
        captured!.Model.Should().Be("task-model");
        captured.Messages[1].Content.ToString().Should().Contain("What was revenue?");
        captured.Messages[1].Content.ToString().Should().Contain("Revenue was $10M.");
    }

    [Fact]
    public async Task AgenticRoleService_AnswerTaskAsync_RecordsPythonCompatibleLlmMetrics()
    {
        var measurements = new List<(string Name, double Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "dotnet-rag-server"
                && instrument.Name.StartsWith("agentic_llm_", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            measurements.Add((instrument.Name, measurement)));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
            measurements.Add((instrument.Name, measurement)));
        listener.Start();

        using var metrics = new RagMetrics();
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse(
                Content: """
                { "completeness": "complete", "answer": "Revenue was $10M.", "missing": "" }
                """,
                Usage: new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = 11,
                    ["completion_tokens"] = 7,
                    ["total_tokens"] = 18
                }));
        var service = BuildRoles(chat.Object, metrics: metrics);

        var result = await service.AnswerTaskAsync(new AgenticTaskAnswerRequest(
            "What was revenue?",
            "Revenue was $10M."));

        result.Completeness.Should().Be("complete");
        measurements.Should().Contain(item => item.Name == "agentic_llm_calls_total" && item.Value == 1);
        measurements.Should().Contain(item => item.Name == "agentic_llm_call_duration_ms" && item.Value >= 0);
        measurements.Should().Contain(item => item.Name == "agentic_llm_tokens_total" && item.Value == 11);
        measurements.Should().Contain(item => item.Name == "agentic_llm_tokens_total" && item.Value == 7);
    }

    [Fact]
    public async Task AgenticRoleService_GenerateSeedQueryAsync_RendersSeedPromptAndParsesDecision()
    {
        ChatCompletionRequest? captured = null;
        var chat = MockRoleChat("""
        { "reasoning": "Try alternate wording.", "seed_query": "annual revenue", "stop": false }
        """, request => captured = request);
        var service = BuildRoles(chat.Object);

        var result = await service.GenerateSeedQueryAsync(new AgenticSeedQueryRequest(
            "What was revenue?",
            "1. Query: \"sales\" -> partial"));

        result.Succeeded.Should().BeTrue();
        result.SeedQuery!.SeedQuery.Should().Be("annual revenue");
        captured.Should().NotBeNull();
        captured!.Model.Should().Be("seed-model");
        captured.Messages[1].Content.ToString().Should().Contain("sales");
    }

    [Fact]
    public async Task AgenticRoleService_SynthesizeAsync_RendersSynthesisPromptAndCleansAnswer()
    {
        ChatCompletionRequest? captured = null;
        var chat = MockRoleChat("**Revenue** was $10M.", request => captured = request);
        var service = BuildRoles(chat.Object);

        var result = await service.SynthesizeAsync(new AgenticSynthesisRequest(
            "What was revenue?",
            "Resolved Query: What was revenue?",
            "Answer directly.",
            "Revenue was $10M."));

        result.Answer.Should().Be("Revenue was $10M.");
        captured.Should().NotBeNull();
        captured!.Model.Should().Be("synthesis-model");
        captured.Messages[1].Content.ToString().Should().Contain("Answer directly.");
        captured.Messages[1].Content.ToString().Should().Contain("Revenue was $10M.");
    }

    [Fact]
    public async Task AgenticRoleService_SynthesizeStreamingAsync_StreamsDeltasAndCleansAnswer()
    {
        ChatCompletionRequest? captured = null;
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.StreamDeltasAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((request, _) => captured = request)
            .Returns(StreamDeltas(
                new ChatStreamDelta(Content: "**Revenue** "),
                new ChatStreamDelta(Content: "was $10M."),
                new ChatStreamDelta(Usage: new Dictionary<string, object?>
                {
                    ["total_tokens"] = 12
                })));
        var service = BuildRoles(chat.Object);
        var deltas = new List<ChatStreamDelta>();

        var result = await service.SynthesizeStreamingAsync(
            new AgenticSynthesisRequest(
                "What was revenue?",
                "Resolved Query: What was revenue?",
                "Answer directly.",
                "Revenue was $10M."),
            deltas.Add);

        result.Answer.Should().Be("Revenue was $10M.");
        deltas.Select(delta => delta.Content).Should().Equal("**Revenue** ", "was $10M.", null);
        captured.Should().NotBeNull();
        captured!.Model.Should().Be("synthesis-model");
        captured.Messages[1].Content.ToString().Should().Contain("Answer directly.");
        chat.Verify(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AgenticRoleService_VerifyAsync_RendersVerificationPromptAndParsesTasks()
    {
        ChatCompletionRequest? captured = null;
        var chat = MockRoleChat("""
        {
          "status": "fail",
          "issues": ["Margin omitted"],
          "tasks": [
            { "id": "v1", "question": "What was margin?", "query": "2024 margin" },
            { "id": "v2", "question": "What was revenue?", "query": "2024 revenue" }
          ]
        }
        """, request => captured = request);
        var service = BuildRoles(chat.Object, verificationMaxTasks: 1);

        var result = await service.VerifyAsync(new AgenticVerificationRequest(
            "Compare revenue and margin.",
            "Resolved Query: Compare revenue and margin.",
            "Revenue was $10M.",
            "t1: revenue answered"));

        result.Succeeded.Should().BeTrue();
        result.Verification!.Passed.Should().BeFalse();
        result.Verification.Tasks.Should().ContainSingle().Which.Id.Should().Be("v1");
        captured.Should().NotBeNull();
        captured!.Model.Should().Be("planner-model");
        captured.Messages[0].Content.ToString().Should().Contain("Maximum 1 tasks");
        captured.Messages[1].Content.ToString().Should().Contain("Revenue was $10M.");
    }

    [Fact]
    public async Task AgenticOrchestrationService_RunOneTaskAsync_ComposesPlannerRetrievalTaskAndSynthesis()
    {
        AgenticPlannerRequest? plannerRequest = null;
        AgenticTaskAnswerRequest? taskRequest = null;
        AgenticSynthesisRequest? synthesisRequest = null;
        var planner = new Mock<IAgenticPlannerService>();
        planner.Setup(p => p.CreatePlanAsync(It.IsAny<AgenticPlannerRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgenticPlannerRequest, CancellationToken>((request, _) => plannerRequest = request)
            .ReturnsAsync(new AgenticPlanParseResult(
                new AgenticPlan(
                    false,
                    "Revenue is in scope.",
                    "What was revenue in 2024?",
                    [new AgenticPlanTask("t1", "What was revenue?", "2024 revenue")],
                    "Answer with the value."),
                null,
                "{}"));

        var roles = new Mock<IAgenticRoleService>();
        roles.Setup(r => r.AnswerTaskAsync(It.IsAny<AgenticTaskAnswerRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgenticTaskAnswerRequest, CancellationToken>((request, _) => taskRequest = request)
            .ReturnsAsync(new AgenticTaskAnswer("complete", "Revenue was $10M.", ""));
        roles.Setup(r => r.SynthesizeAsync(It.IsAny<AgenticSynthesisRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgenticSynthesisRequest, CancellationToken>((request, _) => synthesisRequest = request)
            .ReturnsAsync(new AgenticSynthesisResult("Final: revenue was $10M."));
        roles.Setup(r => r.VerifyAsync(It.IsAny<AgenticVerificationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticVerificationParseResult(
                new AgenticVerification(true, "complete", [], []),
                null,
                "{}"));

        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(v => v.SearchAsync("docs", "What was revenue?", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("initial-1", "Initial revenue context", 0.7, new Dictionary<string, string>
                {
                    ["filename"] = "initial.pdf"
                })
            ]);
        vectorStore.Setup(v => v.SearchAsync("docs", "2024 revenue", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("task-1", "Revenue was $10M.", 0.95, new Dictionary<string, string>
                {
                    ["source"] = "report.pdf"
                })
            ]);
        var service = new AgenticOrchestrationService(
            planner.Object,
            roles.Object,
            vectorStore.Object,
            new RagServerConfiguration { VdbTopK = 10, RerankerTopK = 3 });
        var events = new List<AgenticOrchestrationEvent>();

        var result = await service.RunOneTaskAsync(new AgenticOrchestrationRequest(
            "What was revenue?",
            ["docs"]),
            onEvent: events.Add);

        result.Succeeded.Should().BeTrue();
        result.Answer.Should().Be("Final: revenue was $10M.");
        result.TaskExecutions.Should().ContainSingle();
        result.Citations.Should().ContainSingle().Which.Should().Match<AgenticCitation>(citation =>
            citation.DocumentId == "task-1"
            && citation.TaskId == "t1"
            && citation.Text == "Revenue was $10M."
            && citation.Metadata["source"] == "report.pdf");
        result.Verification!.Passed.Should().BeTrue();
        plannerRequest.Should().NotBeNull();
        plannerRequest!.InitialContext.Should().Contain("Initial revenue context");
        taskRequest.Should().NotBeNull();
        taskRequest!.Question.Should().Be("What was revenue?");
        taskRequest.Documents.Should().Contain("Revenue was $10M.");
        taskRequest.Documents.Should().Contain("source: report.pdf");
        synthesisRequest.Should().NotBeNull();
        synthesisRequest!.ResolvedSection.Should().Contain("Resolved Query: What was revenue in 2024?");
        synthesisRequest.SubAnswers.Should().Contain("Revenue was $10M.");
        events.Select(item => $"{item.EventType}:{item.Stage}").Should().Equal(
            "stage_start:plan",
            "stage_end:plan",
            "stage_start:execute",
            "stage_end:execute",
            "stage_start:synthesize",
            "stage_end:synthesize",
            "stage_start:verify",
            "intermediate_output:verify",
            "stage_end:verify");
    }

    [Fact]
    public async Task AgenticOrchestrationService_RunOneTaskAsync_RecordsAgenticMetrics()
    {
        var measurements = new List<(string Name, double Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "dotnet-rag-server"
                && (instrument.Name.StartsWith("rag_agentic_", StringComparison.Ordinal)
                    || instrument.Name.StartsWith("agentic_", StringComparison.Ordinal)))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            measurements.Add((instrument.Name, measurement)));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
            measurements.Add((instrument.Name, measurement)));
        listener.Start();

        using var metrics = new RagMetrics();
        var planner = new Mock<IAgenticPlannerService>();
        planner.Setup(p => p.CreatePlanAsync(It.IsAny<AgenticPlannerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticPlanParseResult(
                new AgenticPlan(
                    false,
                    "Revenue is in scope.",
                    "What was revenue?",
                    [new AgenticPlanTask("t1", "What was revenue?", "revenue")],
                    "Answer directly."),
                null,
                "{}"));

        var roles = new Mock<IAgenticRoleService>();
        roles.Setup(r => r.AnswerTaskAsync(It.IsAny<AgenticTaskAnswerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticTaskAnswer("complete", "Revenue was $10M.", ""));
        roles.Setup(r => r.SynthesizeAsync(It.IsAny<AgenticSynthesisRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticSynthesisResult("Revenue was $10M."));
        roles.Setup(r => r.VerifyAsync(It.IsAny<AgenticVerificationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticVerificationParseResult(
                new AgenticVerification(true, "complete", [], []),
                null,
                "{}"));

        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(v => v.SearchAsync("docs", "What was revenue?", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<VectorSearchResult>());
        vectorStore.Setup(v => v.SearchAsync("docs", "revenue", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("doc-1", "Revenue was $10M.", 0.95)
            ]);
        var service = new AgenticOrchestrationService(
            planner.Object,
            roles.Object,
            vectorStore.Object,
            new RagServerConfiguration(),
            metrics);

        var result = await service.RunOneTaskAsync(new AgenticOrchestrationRequest(
            "What was revenue?",
            ["docs"]));

        result.Succeeded.Should().BeTrue();
        measurements.Should().Contain(item => item.Name == "rag_agentic_requests_total" && item.Value == 1);
        measurements.Should().Contain(item => item.Name == "rag_agentic_tasks_total" && item.Value == 1);
        measurements.Should().Contain(item => item.Name == "rag_agentic_citations_total" && item.Value == 1);
        measurements.Should().Contain(item => item.Name == "rag_agentic_duration_seconds" && item.Value >= 0);
        measurements.Should().NotContain(item => item.Name == "rag_agentic_errors_total");
        measurements.Should().Contain(item => item.Name == "agentic_requests_total" && item.Value == 1);
        measurements.Should().Contain(item => item.Name == "agentic_request_duration_ms" && item.Value >= 0);
        measurements.Should().Contain(item => item.Name == "agentic_stage_duration_ms" && item.Value >= 0);
        measurements.Should().Contain(item => item.Name == "agentic_plan_tasks" && item.Value == 1);
        measurements.Should().Contain(item => item.Name == "agentic_scope_rounds" && item.Value == 0);
        measurements.Should().Contain(item => item.Name == "agentic_verification_total" && item.Value == 1);
        measurements.Should().Contain(item => item.Name == "agentic_verification_followup_tasks" && item.Value == 0);
        measurements.Should().Contain(item => item.Name == "agentic_retrieval_calls_total" && item.Value == 1);
        measurements.Should().Contain(item => item.Name == "agentic_retrieved_chunks" && item.Value == 0);
        measurements.Should().Contain(item => item.Name == "agentic_retrieved_chunks" && item.Value == 1);
        measurements.Should().Contain(item => item.Name == "agentic_task_results_total" && item.Value == 1);
        measurements.Should().Contain(item => item.Name == "agentic_task_attempts" && item.Value == 1);
        measurements.Should().NotContain(item => item.Name == "agentic_errors_total");
    }

    [Fact]
    public async Task AgenticOrchestrationService_RunOneTaskAsync_ExecutesVerificationFollowupAndResynthesizes()
    {
        var planner = new Mock<IAgenticPlannerService>();
        planner.Setup(p => p.CreatePlanAsync(It.IsAny<AgenticPlannerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticPlanParseResult(
                new AgenticPlan(
                    false,
                    "Revenue and margin are in scope.",
                    "Compare revenue and margin.",
                    [new AgenticPlanTask("t1", "What was revenue?", "revenue")],
                    "Compare the available values."),
                null,
                "{}"));

        var roles = new Mock<IAgenticRoleService>();
        roles.SetupSequence(r => r.AnswerTaskAsync(It.IsAny<AgenticTaskAnswerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticTaskAnswer("partial", "Revenue was $10M.", "Margin"))
            .ReturnsAsync(new AgenticTaskAnswer("complete", "Margin was 42%.", ""));
        roles.SetupSequence(r => r.SynthesizeAsync(It.IsAny<AgenticSynthesisRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticSynthesisResult("Revenue was $10M."))
            .ReturnsAsync(new AgenticSynthesisResult("Revenue was $10M and margin was 42%."));
        roles.Setup(r => r.VerifyAsync(It.IsAny<AgenticVerificationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticVerificationParseResult(
                new AgenticVerification(
                    false,
                    "Margin is missing.",
                    ["Margin omitted"],
                    [new AgenticPlanTask("v1", "What was margin?", "margin")]),
                null,
                "{}"));

        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(v => v.SearchAsync("docs", "Compare revenue and margin.", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<VectorSearchResult>());
        vectorStore.Setup(v => v.SearchAsync("docs", "revenue", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("revenue-1", "Revenue was $10M.", 0.9)
            ]);
        vectorStore.Setup(v => v.SearchAsync("docs", "margin", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("margin-1", "Margin was 42%.", 0.92)
            ]);
        var service = new AgenticOrchestrationService(
            planner.Object,
            roles.Object,
            vectorStore.Object,
            new RagServerConfiguration());

        var result = await service.RunOneTaskAsync(new AgenticOrchestrationRequest(
            "Compare revenue and margin.",
            ["docs"]));

        result.Succeeded.Should().BeTrue();
        result.Answer.Should().Be("Revenue was $10M and margin was 42%.");
        result.TaskExecutions.Should().HaveCount(2);
        result.TaskExecutions[1].Task.Id.Should().Be("v1");
        result.Citations.Should().HaveCount(2);
        result.Citations.Select(citation => citation.DocumentId).Should().Equal("revenue-1", "margin-1");
        result.Citations.Select(citation => citation.TaskId).Should().Equal("t1", "v1");
        result.Verification!.Passed.Should().BeFalse();
        roles.Verify(r => r.SynthesizeAsync(It.IsAny<AgenticSynthesisRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AgenticOrchestrationService_RunOneTaskAsync_ExecutesAllPlannerTasks()
    {
        AgenticSynthesisRequest? synthesisRequest = null;
        var planner = new Mock<IAgenticPlannerService>();
        planner.Setup(p => p.CreatePlanAsync(It.IsAny<AgenticPlannerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticPlanParseResult(
                new AgenticPlan(
                    false,
                    "Project purpose and services are in scope.",
                    "Compare project purpose and backend services.",
                    [
                        new AgenticPlanTask("t1", "What is this project?", "project purpose"),
                        new AgenticPlanTask("t2", "What are its backend services?", "backend services")
                    ],
                    "Combine both task answers."),
                null,
                "{}"));

        var roles = new Mock<IAgenticRoleService>();
        roles.Setup(r => r.AnswerTaskAsync(It.IsAny<AgenticTaskAnswerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgenticTaskAnswerRequest request, CancellationToken _) =>
                request.Question.Contains("backend", StringComparison.OrdinalIgnoreCase)
                    ? new AgenticTaskAnswer("complete", "The backend services are ingestor_server and rag_server.", "")
                    : new AgenticTaskAnswer("complete", "This project is a RAG blueprint.", ""));
        roles.Setup(r => r.SynthesizeAsync(It.IsAny<AgenticSynthesisRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgenticSynthesisRequest, CancellationToken>((request, _) => synthesisRequest = request)
            .ReturnsAsync(new AgenticSynthesisResult(
                "This project is a RAG blueprint with ingestor_server and rag_server backend services."));
        roles.Setup(r => r.VerifyAsync(It.IsAny<AgenticVerificationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticVerificationParseResult(
                new AgenticVerification(true, "complete", [], []),
                null,
                "{}"));

        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(v => v.SearchAsync("docs", "Compare project purpose and backend services.", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<VectorSearchResult>());
        vectorStore.Setup(v => v.SearchAsync("docs", "project purpose", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("purpose-1", "This project is a RAG blueprint.", 0.91)
            ]);
        vectorStore.Setup(v => v.SearchAsync("docs", "backend services", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("services-1", "The backend services are ingestor_server and rag_server.", 0.92)
            ]);
        var service = new AgenticOrchestrationService(
            planner.Object,
            roles.Object,
            vectorStore.Object,
            new RagServerConfiguration());

        var result = await service.RunOneTaskAsync(new AgenticOrchestrationRequest(
            "Compare project purpose and backend services.",
            ["docs"]));

        result.Succeeded.Should().BeTrue();
        result.TaskExecutions.Select(execution => execution.Task.Id).Should().Equal("t1", "t2");
        result.Citations.Select(citation => citation.DocumentId).Should().Equal("purpose-1", "services-1");
        result.Citations.Select(citation => citation.TaskId).Should().Equal("t1", "t2");
        synthesisRequest.Should().NotBeNull();
        synthesisRequest!.SubAnswers.Should().Contain("Task t1");
        synthesisRequest.SubAnswers.Should().Contain("Task t2");
        synthesisRequest.SubAnswers.Should().Contain("This project is a RAG blueprint.");
        synthesisRequest.SubAnswers.Should().Contain("ingestor_server and rag_server");
        roles.Verify(r => r.AnswerTaskAsync(It.IsAny<AgenticTaskAnswerRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AgenticOrchestrationService_RunOneTaskAsync_ReplansAfterScopeDiscovery()
    {
        AgenticPlannerRequest? replanRequest = null;
        var planner = new Mock<IAgenticPlannerService>();
        var plannerCalls = 0;
        planner.Setup(p => p.CreatePlanAsync(It.IsAny<AgenticPlannerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgenticPlannerRequest request, CancellationToken _) =>
            {
                if (plannerCalls++ == 0)
                {
                    return new AgenticPlanParseResult(
                        new AgenticPlan(
                            true,
                            "Need to discover available documents.",
                            "What documents cover revenue?",
                            [new AgenticPlanTask("scope1", "Find revenue documents.", "revenue documents")],
                            string.Empty),
                        null,
                        "{}");
                }

                replanRequest = request;
                return new AgenticPlanParseResult(
                    new AgenticPlan(
                        false,
                        "Revenue document found.",
                        "What was revenue?",
                        [new AgenticPlanTask("t1", "What was revenue?", "revenue")],
                        "Answer directly."),
                    null,
                    "{}");
            });

        var roles = new Mock<IAgenticRoleService>();
        roles.Setup(r => r.AnswerTaskAsync(It.IsAny<AgenticTaskAnswerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticTaskAnswer("complete", "Revenue was $10M.", ""));
        roles.Setup(r => r.SynthesizeAsync(It.IsAny<AgenticSynthesisRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticSynthesisResult("Revenue was $10M."));
        roles.Setup(r => r.VerifyAsync(It.IsAny<AgenticVerificationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticVerificationParseResult(
                new AgenticVerification(true, "complete", [], []),
                null,
                "{}"));

        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(v => v.SearchAsync("docs", "Find revenue", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<VectorSearchResult>());
        vectorStore.Setup(v => v.SearchAsync("docs", "revenue documents", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("scope-doc", "Annual report contains revenue.", 0.88)
            ]);
        vectorStore.Setup(v => v.SearchAsync("docs", "revenue", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("task-doc", "Revenue was $10M.", 0.95)
            ]);
        var service = new AgenticOrchestrationService(
            planner.Object,
            roles.Object,
            vectorStore.Object,
            new RagServerConfiguration { AgenticPlannerMaxScopeRounds = 1 });

        var result = await service.RunOneTaskAsync(new AgenticOrchestrationRequest(
            "Find revenue",
            ["docs"]));

        result.Succeeded.Should().BeTrue();
        result.Answer.Should().Be("Revenue was $10M.");
        replanRequest.Should().NotBeNull();
        replanRequest!.ScopeResults.Should().ContainKey("scope1");
        replanRequest.ScopeResults!["scope1"].Should().Contain("Annual report contains revenue.");
        result.Citations.Should().ContainSingle().Which.DocumentId.Should().Be("task-doc");
    }

    [Fact]
    public async Task AgenticOrchestrationService_RunOneTaskAsync_RetriesPartialTaskWithSeedQuery()
    {
        AgenticSeedQueryRequest? seedRequest = null;
        AgenticTaskAnswerRequest? retryTaskRequest = null;
        var planner = new Mock<IAgenticPlannerService>();
        planner.Setup(p => p.CreatePlanAsync(It.IsAny<AgenticPlannerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticPlanParseResult(
                new AgenticPlan(
                    false,
                    "Revenue and margin are in scope.",
                    "Compare revenue and margin.",
                    [new AgenticPlanTask("t1", "Compare revenue and margin.", "revenue")],
                    "Compare values."),
                null,
                "{}"));

        var roles = new Mock<IAgenticRoleService>();
        var answerCalls = 0;
        roles.Setup(r => r.AnswerTaskAsync(It.IsAny<AgenticTaskAnswerRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgenticTaskAnswerRequest, CancellationToken>((request, _) =>
            {
                if (answerCalls == 1)
                {
                    retryTaskRequest = request;
                }

                answerCalls++;
            })
            .ReturnsAsync(() => answerCalls == 1
                ? new AgenticTaskAnswer("partial", "Revenue was $10M.", "Margin")
                : new AgenticTaskAnswer("complete", "Revenue was $10M and margin was 42%.", ""));
        roles.Setup(r => r.GenerateSeedQueryAsync(It.IsAny<AgenticSeedQueryRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgenticSeedQueryRequest, CancellationToken>((request, _) => seedRequest = request)
            .ReturnsAsync(new AgenticSeedQueryParseResult(
                new AgenticSeedQuery("Need margin.", "margin", false),
                null,
                "{}"));
        roles.Setup(r => r.SynthesizeAsync(It.IsAny<AgenticSynthesisRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticSynthesisResult("Revenue was $10M and margin was 42%."));
        roles.Setup(r => r.VerifyAsync(It.IsAny<AgenticVerificationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticVerificationParseResult(
                new AgenticVerification(true, "complete", [], []),
                null,
                "{}"));

        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(v => v.SearchAsync("docs", "Compare revenue and margin.", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<VectorSearchResult>());
        vectorStore.Setup(v => v.SearchAsync("docs", "revenue", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("revenue-1", "Revenue was $10M.", 0.91)
            ]);
        vectorStore.Setup(v => v.SearchAsync("docs", "margin", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VectorSearchResult("margin-1", "Margin was 42%.", 0.93)
            ]);
        var service = new AgenticOrchestrationService(
            planner.Object,
            roles.Object,
            vectorStore.Object,
            new RagServerConfiguration { AgenticTaskAnswerMaxRetries = 2 });

        var result = await service.RunOneTaskAsync(new AgenticOrchestrationRequest(
            "Compare revenue and margin.",
            ["docs"]));

        result.Succeeded.Should().BeTrue();
        result.TaskExecutions.Should().ContainSingle();
        result.TaskExecutions[0].Answer.Completeness.Should().Be("complete");
        result.TaskExecutions[0].Documents.Select(document => document.Id).Should().Equal("revenue-1", "margin-1");
        result.Citations.Select(citation => citation.DocumentId).Should().Equal("margin-1", "revenue-1");
        seedRequest.Should().NotBeNull();
        seedRequest!.TriedQueries.Should().Contain("PARTIAL");
        retryTaskRequest.Should().NotBeNull();
        retryTaskRequest!.Question.Should().Contain("prior retrieval already found this partial answer");
        roles.Verify(r => r.GenerateSeedQueryAsync(It.IsAny<AgenticSeedQueryRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgenticOrchestrationService_RunOneTaskAsync_ReturnsPlannerFailureWithoutRoleCalls()
    {
        var planner = new Mock<IAgenticPlannerService>();
        planner.Setup(p => p.CreatePlanAsync(It.IsAny<AgenticPlannerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticPlanParseResult(null, "bad plan", "raw"));
        var roles = new Mock<IAgenticRoleService>();
        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(v => v.SearchAsync("docs", "q", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<VectorSearchResult>());
        var service = new AgenticOrchestrationService(
            planner.Object,
            roles.Object,
            vectorStore.Object,
            new RagServerConfiguration());

        var result = await service.RunOneTaskAsync(new AgenticOrchestrationRequest("q", ["docs"]));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("bad plan");
        roles.Verify(r => r.AnswerTaskAsync(It.IsAny<AgenticTaskAnswerRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        roles.Verify(r => r.SynthesizeAsync(It.IsAny<AgenticSynthesisRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static IServiceProvider BuildAgenticServiceProvider(IAgenticOrchestrationService orchestration)
        => new ServiceCollection()
            .AddSingleton(orchestration)
            .BuildServiceProvider();

    private static AgenticPlannerService BuildPlanner(
        IChatCompletionService chat,
        int maxTasks = 5,
        int maxAttempts = 3,
        RagMetrics? metrics = null)
    {
        var config = new RagServerConfiguration
        {
            AgenticPlannerModel = "planner-model",
            AgenticPlannerMaxTasks = maxTasks,
            AgenticPlannerMaxAttempts = maxAttempts
        };
        return new AgenticPlannerService(
            chat,
            config,
            PromptCatalog.Load(null),
            NullLogger<AgenticPlannerService>.Instance,
            metrics);
    }

    private static AgenticRoleService BuildRoles(
        IChatCompletionService chat,
        int verificationMaxTasks = 3,
        RagMetrics? metrics = null)
    {
        var config = new RagServerConfiguration
        {
            AgenticPlannerModel = "planner-model",
            AgenticTaskModel = "task-model",
            AgenticSeedGenerationModel = "seed-model",
            AgenticSynthesisModel = "synthesis-model",
            AgenticVerificationMaxTasks = verificationMaxTasks
        };
        return new AgenticRoleService(chat, config, PromptCatalog.Load(null), metrics);
    }

    private static Mock<IChatCompletionService> MockRoleChat(
        string response,
        Action<ChatCompletionRequest> capture)
    {
        var chat = new Mock<IChatCompletionService>();
        chat.Setup(c => c.CompleteAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((request, _) => capture(request))
            .ReturnsAsync(new ChatCompletionResponse(response));
        return chat;
    }

    private static async IAsyncEnumerable<ChatStreamDelta> StreamDeltas(params ChatStreamDelta[] deltas)
    {
        foreach (var delta in deltas)
        {
            await Task.Yield();
            yield return delta;
        }
    }
}
