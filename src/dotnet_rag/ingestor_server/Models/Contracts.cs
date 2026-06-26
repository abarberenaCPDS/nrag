namespace DotnetRag.Ingestor.Models;

public sealed class SplitOptions
{
    public int ChunkSize { get; set; } = 2048;
    public int ChunkOverlap { get; set; } = 150;
}

public sealed class CustomMetadata
{
    public string Filename { get; set; } = string.Empty;
    public Dictionary<string, object?> Metadata { get; set; } = [];
}

public sealed class DocumentCatalogMetadata
{
    public string Filename { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string>? Tags { get; set; }
}

public sealed class SummaryOptions
{
    public object? PageFilter { get; set; }
    public bool ShallowSummary { get; set; }
    public string? SummarizationStrategy { get; set; }
}

public sealed class PdfSplitProcessingOptions
{
    public int PagesPerChunk { get; set; } = 4;
}

public sealed class DocumentUploadRequest
{
    public string VdbEndpoint { get; set; } =
        Environment.GetEnvironmentVariable("APP_VECTORSTORE_URL") ?? "http://localhost:8000";

    public string CollectionName { get; set; } =
        Environment.GetEnvironmentVariable("COLLECTION_NAME") ?? "multimodal_data";

    public bool Blocking { get; set; }
    public SplitOptions SplitOptions { get; set; } = new();
    public List<CustomMetadata> CustomMetadata { get; set; } = [];
    public bool GenerateSummary { get; set; }
    public List<DocumentCatalogMetadata> DocumentsCatalogMetadata { get; set; } = [];
    public SummaryOptions? SummaryOptions { get; set; }
    public bool EnablePdfSplitProcessing { get; set; }
    public PdfSplitProcessingOptions PdfSplitProcessingOptions { get; set; } = new();
}

public sealed class UploadedDocument
{
    public string DocumentId { get; set; } = string.Empty;
    public string DocumentName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public Dictionary<string, object?> Metadata { get; set; } = [];
    public Dictionary<string, object?> DocumentInfo { get; set; } = [];
}

public sealed class FailedDocument
{
    public string DocumentName { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}

public sealed class UploadDocumentResponse
{
    public string Message { get; set; } = string.Empty;
    public int TotalDocuments { get; set; }
    public int DocumentsCompleted { get; set; }
    public int BatchesCompleted { get; set; }
    public List<UploadedDocument> Documents { get; set; } = [];
    public List<FailedDocument> FailedDocuments { get; set; } = [];
    public List<Dictionary<string, object?>> ValidationErrors { get; set; } = [];
}

public sealed class UploadValidationErrorResponse
{
    public string Message { get; set; } = string.Empty;
    public int TotalDocuments { get; set; }
}

public sealed class IngestionTaskResponse
{
    public string Message { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
}

public sealed class NvIngestStatusResponse
{
    public int ExtractionCompleted { get; set; }
    public Dictionary<string, string> DocumentWiseStatus { get; set; } = [];
}

public sealed class IngestionTaskStatusResponse
{
    public string State { get; set; } = "UNKNOWN";
    public UploadDocumentResponse Result { get; set; } = new();
    public NvIngestStatusResponse NvIngestStatus { get; set; } = new();
}

public sealed class DocumentListResponse
{
    public string Message { get; set; } = string.Empty;
    public int TotalDocuments { get; set; }
    public List<UploadedDocument> Documents { get; set; } = [];
}

public sealed class MetadataField
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public string Description { get; set; } = string.Empty;
    public bool Required { get; set; }
    public bool UserDefined { get; set; } = true;
    public bool SupportDynamicFiltering { get; set; } = true;
    public string? ArrayType { get; set; }
    public int? MaxLength { get; set; }
}

public sealed class UploadedCollection
{
    public string CollectionName { get; set; } = string.Empty;
    public int NumEntities { get; set; }
    public List<Dictionary<string, object?>> MetadataSchema { get; set; } = [];
    public Dictionary<string, object?> CollectionInfo { get; set; } = [];
}

public sealed class CollectionListResponse
{
    public string Message { get; set; } = string.Empty;
    public int TotalCollections { get; set; }
    public List<UploadedCollection> Collections { get; set; } = [];
}

public sealed class CreateCollectionRequest
{
    public string VdbEndpoint { get; set; } =
        Environment.GetEnvironmentVariable("APP_VECTORSTORE_URL") ?? "http://localhost:8000";

    public string CollectionName { get; set; } =
        Environment.GetEnvironmentVariable("COLLECTION_NAME") ?? "multimodal_data";

    public List<MetadataField> MetadataSchema { get; set; } = [];
    public string Description { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public string Owner { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string BusinessDomain { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
}

public sealed class FailedCollection
{
    public string CollectionName { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}

public sealed class CollectionsResponse
{
    public string Message { get; set; } = string.Empty;
    public List<string> Successful { get; set; } = [];
    public List<FailedCollection> Failed { get; set; } = [];
    public int TotalSuccess { get; set; }
    public int TotalFailed { get; set; }
}

public sealed class CreateCollectionResponse
{
    public string Message { get; set; } = string.Empty;
    public string CollectionName { get; set; } = string.Empty;
}

public sealed class UpdateCollectionMetadataRequest
{
    public string? Description { get; set; }
    public List<string>? Tags { get; set; }
    public string? Owner { get; set; }
    public string? BusinessDomain { get; set; }
    public string? Status { get; set; }
}

public sealed class UpdateDocumentMetadataRequest
{
    public string? Description { get; set; }
    public List<string>? Tags { get; set; }
}

public sealed class UpdateMetadataResponse
{
    public string Message { get; set; } = string.Empty;
    public string CollectionName { get; set; } = string.Empty;
}

public sealed class IngestorHealthResponse
{
    public string Message { get; set; } = "Service is up.";
    public List<Dictionary<string, object?>> Databases { get; set; } = [];
    public List<Dictionary<string, object?>> ObjectStorage { get; set; } = [];
    public List<Dictionary<string, object?>> Nim { get; set; } = [];
    public List<Dictionary<string, object?>> Processing { get; set; } = [];
    public List<Dictionary<string, object?>> TaskManagement { get; set; } = [];
}
