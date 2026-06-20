using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetRag.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using ChatCompletionRequest = DotnetRag.Shared.Abstractions.ChatCompletionRequest;
using ChatCompletionResponse = DotnetRag.Shared.Abstractions.ChatCompletionResponse;
using IChatCompletionService = DotnetRag.Shared.Abstractions.IChatCompletionService;

namespace DotnetRag.Shared.LlmProviders;

/// <summary>
/// Calls Ollama /api/chat to fulfill chat completion requests.
/// ORIG_LLM_MODEL: nvidia/nemotron-3-super-120b-a12b (NIM endpoint)
/// Default local model: qwen2.5:3b (3-billion parameter nano model)
/// ORIG_LLM_ENDPOINT: nim-llm:8000
/// </summary>
public sealed class OllamaChatCompletionService : IChatCompletionService
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    private readonly HttpClient _http;
    private readonly string _chatEndpoint;
    private readonly string _defaultModel;
    private readonly ILogger<OllamaChatCompletionService> _logger;

    public OllamaChatCompletionService(
        IHttpClientFactory httpClientFactory,
        string defaultModel,
        string ollamaBaseUrl,
        ILogger<OllamaChatCompletionService> logger)
    {
        _http = httpClientFactory.CreateClient("ollama");
        _chatEndpoint = ollamaBaseUrl.TrimEnd('/') + "/api/chat";
        _defaultModel = defaultModel;
        _logger = logger;
    }

    public async Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) ? _defaultModel : request.Model;

        var messages = request.Messages.Select(m => new
        {
            role = m.Role,
            content = m.Content
        }).ToList();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = false,
            ["options"] = new Dictionary<string, object?>
            {
                ["temperature"] = request.Temperature ?? 0.0,
                ["top_p"] = request.TopP ?? 1.0,
                ["num_predict"] = request.MaxTokens ?? 4096
            }
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.LogDebug("Ollama chat → model={Model} endpoint={Endpoint}", model, _chatEndpoint);

        var response = await _http.PostAsync(_chatEndpoint, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Ollama /api/chat failed ({StatusCode}): {Body}",
                (int)response.StatusCode,
                errorBody);
            response.EnsureSuccessStatusCode();
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var root = JsonNode.Parse(responseBody)?.AsObject();
        var text = root?["message"]?["content"]?.GetValue<string>() ?? string.Empty;

        var usage = new Dictionary<string, object?>
        {
            ["prompt_tokens"] = root?["prompt_eval_count"]?.GetValue<int?>() ?? 0,
            ["completion_tokens"] = root?["eval_count"]?.GetValue<int?>() ?? 0,
            ["total_tokens"] =
                (root?["prompt_eval_count"]?.GetValue<int?>() ?? 0)
                + (root?["eval_count"]?.GetValue<int?>() ?? 0)
        };

        return new ChatCompletionResponse(Content: text, Usage: usage);
    }

    // Streams tokens from Ollama's ndjson streaming API.
    // Each line from Ollama: {"message":{"content":"token"},"done":false}
    public async IAsyncEnumerable<string> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) ? _defaultModel : request.Model;

        var messages = request.Messages.Select(m => new { role = m.Role, content = m.Content }).ToList();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true,
            ["options"] = new Dictionary<string, object?>
            {
                ["temperature"] = request.Temperature ?? 0.0,
                ["top_p"] = request.TopP ?? 1.0,
                ["num_predict"] = request.MaxTokens ?? 4096
            }
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, _chatEndpoint) { Content = httpContent };
        using var response = await _http.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Ollama /api/chat stream failed ({Status}): {Body}", (int)response.StatusCode, errBody);
            response.EnsureSuccessStatusCode();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(line)) continue;

            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch { continue; }

            var token = node?["message"]?["content"]?.GetValue<string>() ?? string.Empty;
            var done = node?["done"]?.GetValue<bool>() == true;

            if (!string.IsNullOrEmpty(token))
                yield return token;

            if (done) break;
        }
    }
}
