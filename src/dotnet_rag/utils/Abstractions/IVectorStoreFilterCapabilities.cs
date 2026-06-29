namespace DotnetRag.Shared.Abstractions;

public enum GeneratedFilterPromptKind
{
    None,
    Milvus,
    Elasticsearch
}

public interface IVectorStoreFilterCapabilities
{
    bool SupportsGeneratedFilters { get; }
    GeneratedFilterPromptKind GeneratedFilterPromptKind { get; }

    Task<string> GetFilterSchemaDescriptionAsync(
        string collectionName,
        CancellationToken cancellationToken = default);
}
