using System.Net.Http.Json;
using System.Text.Json;
using DotnetRag.Blazor.Models;

namespace DotnetRag.Blazor.Services;

public sealed class IngestorApiService(HttpClient http)
{
    public async Task<CollectionListResponse?> GetCollectionsAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<CollectionListResponse>("/collections", ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> CreateCollectionAsync(CreateCollectionRequest request, CancellationToken ct = default)
    {
        try
        {
            request.MetadataSchema = request.MetadataSchema
                .Select(field => field.Normalized())
                .ToList();
            var resp = await http.PostAsJsonAsync("/collection", request, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> DeleteCollectionsAsync(IEnumerable<string> names, CancellationToken ct = default)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Delete, "/collections")
            {
                Content = JsonContent.Create(names.ToList())
            };
            var resp = await http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<DocumentListResponse> GetDocumentsAsync(
        string collectionName, bool forceGetMetadata = false, CancellationToken ct = default)
    {
        try
        {
            var flag = forceGetMetadata ? "true" : "false";
            return await http.GetFromJsonAsync<DocumentListResponse>(
                       $"/documents?collection_name={Uri.EscapeDataString(collectionName)}&force_get_metadata={flag}", ct)
                   ?? new DocumentListResponse();
        }
        catch
        {
            return new DocumentListResponse();
        }
    }

    public async Task<bool> DeleteDocumentsAsync(
        string collectionName, IEnumerable<string> docNames, CancellationToken ct = default)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Delete,
                $"/documents?collection_name={Uri.EscapeDataString(collectionName)}")
            {
                Content = JsonContent.Create(docNames.ToList())
            };
            var resp = await http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IngestionTaskResponse?> UploadDocumentsAsync(
        string collectionName,
        IEnumerable<UploadFile> files,
        List<MetadataFieldDef> schema,
        string? description,
        List<string>? tags,
        bool generateSummary,
        CancellationToken ct = default)
    {
        try
        {
            var filesList = files.ToList();
            using var form = new MultipartFormDataContent();

            var uploadRequest = new
            {
                collection_name = collectionName,
                blocking = false,
                generate_summary = generateSummary,
                documents_catalog_metadata = filesList.Select(f => new
                {
                    filename = f.Name,
                    description = description,
                    tags = tags ?? new List<string>()
                }).ToList()
            };
            form.Add(new StringContent(JsonSerializer.Serialize(uploadRequest)), "data");

            foreach (var file in filesList)
            {
                if (file.Data is null) continue;
                var content = new ByteArrayContent(file.Data);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    file.ContentType.Length > 0 ? file.ContentType : "application/octet-stream");
                form.Add(content, "documents", file.Name);
            }

            var resp = await http.PostAsync("/documents", form, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<IngestionTaskResponse>(ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IngestionTaskStatus?> GetTaskStatusAsync(string taskId, CancellationToken ct = default)
    {
        return await http.GetFromJsonAsync<IngestionTaskStatus>(
            $"/status?task_id={Uri.EscapeDataString(taskId)}", ct);
    }

    public async Task<bool> UpdateDocumentMetadataAsync(
        string collectionName, string documentName,
        string? description, List<string>? tags, CancellationToken ct = default)
    {
        try
        {
            var payload = new { description, tags };
            var resp = await http.PatchAsJsonAsync(
                $"/collections/{Uri.EscapeDataString(collectionName)}/documents/{Uri.EscapeDataString(documentName)}/metadata",
                payload, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
