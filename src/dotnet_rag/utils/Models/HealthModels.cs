namespace DotnetRag.Shared.Models;

public enum ServiceStatus
{
    Healthy,
    Unhealthy,
    Skipped,
    Timeout,
    Error,
    Unknown
}

public record BaseServiceHealthInfo(
    string Service,
    string Url,
    ServiceStatus Status,
    double LatencyMs = 0,
    string? Error = null);

public sealed record DatabaseHealthInfo(
    string Service,
    string Url,
    ServiceStatus Status,
    double LatencyMs = 0,
    string? Error = null,
    int? Collections = null)
    : BaseServiceHealthInfo(Service, Url, Status, LatencyMs, Error);

public sealed record StorageHealthInfo(
    string Service,
    string Url,
    ServiceStatus Status,
    double LatencyMs = 0,
    string? Error = null,
    int? Buckets = null,
    string? Message = null)
    : BaseServiceHealthInfo(Service, Url, Status, LatencyMs, Error);

public sealed record NIMServiceHealthInfo(
    string Service,
    string Url,
    ServiceStatus Status,
    double LatencyMs = 0,
    string? Error = null,
    string? Model = null,
    string? Message = null,
    int? HttpStatus = null)
    : BaseServiceHealthInfo(Service, Url, Status, LatencyMs, Error);

public sealed record ProcessingHealthInfo(
    string Service,
    string Url,
    ServiceStatus Status,
    double LatencyMs = 0,
    string? Error = null,
    int? HttpStatus = null)
    : BaseServiceHealthInfo(Service, Url, Status, LatencyMs, Error);

public sealed record TaskManagementHealthInfo(
    string Service,
    string Url,
    ServiceStatus Status,
    double LatencyMs = 0,
    string? Error = null,
    string? Message = null)
    : BaseServiceHealthInfo(Service, Url, Status, LatencyMs, Error);

public record HealthResponseBase(
    string Message = "Service is up.",
    IReadOnlyList<DatabaseHealthInfo>? Databases = null,
    IReadOnlyList<StorageHealthInfo>? ObjectStorage = null,
    IReadOnlyList<NIMServiceHealthInfo>? Nim = null);

public sealed record RAGHealthResponse(
    string Message = "Service is up.",
    IReadOnlyList<DatabaseHealthInfo>? Databases = null,
    IReadOnlyList<StorageHealthInfo>? ObjectStorage = null,
    IReadOnlyList<NIMServiceHealthInfo>? Nim = null)
    : HealthResponseBase(Message, Databases, ObjectStorage, Nim);

public sealed record IngestorHealthResponse(
    string Message = "Service is up.",
    IReadOnlyList<DatabaseHealthInfo>? Databases = null,
    IReadOnlyList<StorageHealthInfo>? ObjectStorage = null,
    IReadOnlyList<NIMServiceHealthInfo>? Nim = null,
    IReadOnlyList<ProcessingHealthInfo>? Processing = null,
    IReadOnlyList<TaskManagementHealthInfo>? TaskManagement = null)
    : HealthResponseBase(Message, Databases, ObjectStorage, Nim);
