using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Text.Json;

namespace DotnetRag.Tests.Integration;

/// <summary>
/// Smoke-tests the /health endpoint using an in-process WebApplicationFactory.
/// Requires Ollama and ChromaDB to NOT be running (offline mode) — health check
/// should still return 200 OK with service-is-up message.
/// </summary>
public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task GetHealth_Returns200_WithoutDependencyCheck()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var hasExpectedContent = body.Contains("service_is_up")
            || body.Contains("Service is up", StringComparison.OrdinalIgnoreCase)
            || (body.StartsWith("{") && JsonDocument.Parse(body).RootElement.TryGetProperty("message", out _));
        hasExpectedContent.Should().BeTrue("health response should contain status information");
    }

    [Fact]
    public async Task GetV1Health_Returns200()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetMetrics_Returns200_WithPrometheusContentType()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        contentType.Should().StartWith("text/plain");
    }

    [Fact]
    public async Task GetConfiguration_Returns200_WithConfigShape()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/configuration");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("rag_configuration", out _).Should().BeTrue();
    }
}
