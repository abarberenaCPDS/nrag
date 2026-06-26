using System.Net.Http.Headers;
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
/// OpenAI-compatible chat completion client — works with NVIDIA NIM,
/// Azure OpenAI, and any /v1/chat/completions endpoint.
/// ORIG_LLM_MODEL: nvidia/nemotron-3-super-120b-a12b
/// ORIG_LLM_ENDPOINT: nim-llm:8000
/// </summary>
public sealed class OpenAiChatCompletionService : IChatCompletionService
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    private readonly HttpClient _http;
    private readonly string _chatEndpoint;
    private readonly string _defaultModel;
    private readonly string? _apiKey;
    private readonly ILogger<OpenAiChatCompletionService> _logger;

    public OpenAiChatCompletionService(
        IHttpClientFactory httpClientFactory,
        string defaultModel,
        string baseUrl,
        string? apiKey,
        ILogger<OpenAiChatCompletionService> logger)
    {
        _http = httpClientFactory.CreateClient("openai");
        _chatEndpoint = NormalizeChatEndpoint(baseUrl);
        _defaultModel = defaultModel;
        _apiKey = apiKey;
        _logger = logger;
    }

    public async Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) ? _defaultModel : request.Model;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = request.Messages.Select(m => new
            {
                role = m.Role,
                content = m.Content
            }).ToList(),
            ["stream"] = false,
            ["temperature"] = request.Temperature ?? 0.0,
            ["top_p"] = request.TopP ?? 1.0,
            ["max_tokens"] = request.MaxTokens ?? 16256
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _chatEndpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        _logger.LogDebug("OpenAI chat → model={Model} endpoint={Endpoint}", model, _chatEndpoint);

        var response = await _http.SendAsync(httpRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "OpenAI-compatible chat call failed ({StatusCode}): {Body}",
                (int)response.StatusCode,
                errorBody);
            response.EnsureSuccessStatusCode();
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var root = JsonNode.Parse(responseBody)?.AsObject();
        var text = root?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? string.Empty;

        var usageNode = root?["usage"];
        var usage = usageNode is null
            ? null
            : new Dictionary<string, object?>
            {
                ["prompt_tokens"] = usageNode["prompt_tokens"]?.GetValue<int?>() ?? 0,
                ["completion_tokens"] = usageNode["completion_tokens"]?.GetValue<int?>() ?? 0,
                ["total_tokens"] = usageNode["total_tokens"]?.GetValue<int?>() ?? 0
            };

        return new ChatCompletionResponse(Content: text, Usage: usage);
    }

    // Streams tokens from OpenAI-compatible SSE: "data: {...}\n\n", terminated by "data: [DONE]"
    public async IAsyncEnumerable<string> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) ? _defaultModel : request.Model;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = request.Messages.Select(m => new { role = m.Role, content = m.Content }).ToList(),
            ["stream"] = true,
            ["temperature"] = request.Temperature ?? 0.0,
            ["top_p"] = request.TopP ?? 1.0,
            ["max_tokens"] = request.MaxTokens ?? 16256
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, _chatEndpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_apiKey))
            reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("OpenAI-compatible stream failed ({Status}): {Body}", (int)response.StatusCode, errBody);
            response.EnsureSuccessStatusCode();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            JsonNode? node;
            try { node = JsonNode.Parse(data); }
            catch { continue; }

            var token = node?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>() ?? string.Empty;
            if (!string.IsNullOrEmpty(token))
                yield return token;

            var finishReason = node?["choices"]?[0]?["finish_reason"]?.GetValue<string>();
            if (finishReason == "stop") break;
        }
    }

    private static string NormalizeChatEndpoint(string url)
    {
        var normalized = url.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"http://{normalized}";
        }

        normalized = normalized.TrimEnd('/');
        if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return $"{normalized}/chat/completions";
        }

        return $"{normalized}/v1/chat/completions";
    }
}
