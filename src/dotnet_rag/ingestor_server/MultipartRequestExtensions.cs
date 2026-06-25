namespace dotnet_rag.ingestor_server;
using System.Text.Json;
using DotnetRag.Ingestor.Models;

internal static class MultipartRequestExtensions
{
    private static readonly JsonSerializerOptions SnakeCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static async Task<MultipartUploadParseResult> ReadMultipartPayloadAsync(
        this HttpRequest request)
    {
        if (!request.HasFormContentType)
        {
            return new MultipartUploadParseResult(
                false,
                Results.BadRequest(new { message = "Expected multipart/form-data payload." }),
                null,
                null);
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync();
        }
        catch
        {
            return new MultipartUploadParseResult(
                false,
                Results.BadRequest(new { message = "Unable to parse multipart form payload." }),
                null,
                null);
        }

        var files = form.Files.GetFiles("documents");
        if (files.Count == 0)
        {
            return new MultipartUploadParseResult(
                false,
                Results.BadRequest(new { message = "No files provided for uploading." }),
                null,
                null);
        }

        if (!form.TryGetValue("data", out var dataValues) || dataValues.Count == 0)
        {
            return new MultipartUploadParseResult(
                false,
                Results.BadRequest(new { message = "Form field 'data' is required." }),
                null,
                null);
        }

        var dataString = dataValues[0]?.ToString();
        if (string.IsNullOrWhiteSpace(dataString))
        {
            return new MultipartUploadParseResult(
                false,
                Results.UnprocessableEntity(new { message = "Invalid upload payload." }),
                null,
                null);
        }

        DocumentUploadRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<DocumentUploadRequest>(dataString, SnakeCaseOptions);
        }
        catch (JsonException)
        {
            return new MultipartUploadParseResult(
                false,
                Results.BadRequest(new { message = "Invalid JSON format in 'data' field." }),
                null,
                null);
        }

        if (payload is null)
        {
            return new MultipartUploadParseResult(
                false,
                Results.UnprocessableEntity(new { message = "Invalid upload payload." }),
                null,
                null);
        }

        return new MultipartUploadParseResult(true, null, files, payload);
    }
}
