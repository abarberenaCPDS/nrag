namespace DotnetRag.Ingestor.Services;

public interface IIngestorCatalogStore
{
    IReadOnlyList<IngestorCatalogEntry> Load();
    void Save(IReadOnlyList<IngestorCatalogEntry> entries);
}

public sealed class DisabledIngestorCatalogStore : IIngestorCatalogStore
{
    public IReadOnlyList<IngestorCatalogEntry> Load() => [];

    public void Save(IReadOnlyList<IngestorCatalogEntry> entries)
    {
    }
}

public sealed class IngestorCatalogEntry
{
    public required string Name { get; init; }
    public string Description { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public string Owner { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string BusinessDomain { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public List<Dictionary<string, object?>> MetadataSchema { get; set; } = [];
    public List<InMemoryIngestorStore.StoredDocument> Documents { get; set; } = [];
    public object SyncRoot { get; } = new();
}
