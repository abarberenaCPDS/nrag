namespace DotnetRag.Shared.Options;

public sealed class TelemetryOptions
{
    public bool Enabled { get; init; } = true;
    public string ServiceName { get; init; } = "dotnet-rag";
    public string? OtlpHttpEndpoint { get; init; }
    public string? OtlpGrpcEndpoint { get; init; }
    public string? DataDogAgentHost { get; init; }
    public int? DataDogAgentPort { get; init; }
}
