using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DotnetRag.Blazor.Models;

namespace DotnetRag.Blazor.Services;

public sealed class StreamingService(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async IAsyncEnumerable<StreamChunk> StreamGenerateAsync(
        GenerateRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Send request and handle HTTP-level errors outside the yield loop
        HttpResponseMessage? resp = null;
        string? httpError = null;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/v1/generate")
            {
                Content = JsonContent.Create(request)
            };
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            httpError = ex.Message;
        }

        if (httpError is not null)
        {
            yield return ErrorChunk(httpError);
            yield break;
        }

        using var stream = await resp!.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null && !ct.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var json = line["data: ".Length..];
            if (json == "[DONE]") yield break;

            StreamChunk? chunk = null;
            try { chunk = JsonSerializer.Deserialize<StreamChunk>(json, JsonOpts); }
            catch { /* malformed — skip */ }

            if (chunk is not null)
                yield return chunk;
        }
    }

    private static StreamChunk ErrorChunk(string message) => new()
    {
        EventType = "__error__",
        Choices = [new StreamChoice { Delta = new StreamDelta { Content = message } }]
    };
}
