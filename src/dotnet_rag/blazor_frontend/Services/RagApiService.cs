using System.Net.Http.Json;
using DotnetRag.Blazor.Models;

namespace DotnetRag.Blazor.Services;

public sealed class RagApiService(HttpClient http)
{
    public async Task<HealthResponse?> GetHealthAsync(bool checkDependencies = true, CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<HealthResponse>(
                $"/v1/health?check_dependencies={checkDependencies}", ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ConfigurationResponse?> GetConfigurationAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<ConfigurationResponse>("/v1/configuration", ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<SummaryResponse?> GetSummaryAsync(
        string collectionName, string fileName, CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<SummaryResponse>(
                $"/v1/summary?collection_name={Uri.EscapeDataString(collectionName)}&file_name={Uri.EscapeDataString(fileName)}&blocking=false", ct);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class SummaryResponse
{
    public string? Status { get; set; }
    public string? Summary { get; set; }
}
