using DotnetRag.Ingestor.Models;
using DotnetRag.Ingestor.Services;
using FluentAssertions;

namespace DotnetRag.Tests.Unit.Services;

public sealed class InMemoryIngestorStoreTests
{
    private static InMemoryIngestorStore CreateWithSchema(IEnumerable<MetadataField> fields)
    {
        var store = new InMemoryIngestorStore();
        store.CreateCollection(new CreateCollectionRequest
        {
            CollectionName = "test_col",
            MetadataSchema = fields.ToList()
        });
        return store;
    }

    // ── Schema validation ─────────────────────────────────────────────────────

    [Fact]
    public void ValidateDocumentMetadata_ReturnsEmpty_WhenNoSchemaRegistered()
    {
        var store = new InMemoryIngestorStore();
        store.CreateCollection(new CreateCollectionRequest { CollectionName = "col" });

        var errors = store.ValidateDocumentMetadata("col", "doc.pdf",
            new Dictionary<string, object?> { ["anything"] = "value" });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateDocumentMetadata_ReturnsError_ForMissingRequiredField()
    {
        var store = CreateWithSchema([new MetadataField { Name = "author", Type = "str", Required = true }]);

        var errors = store.ValidateDocumentMetadata("test_col", "doc.pdf", new Dictionary<string, object?>());

        errors.Should().ContainSingle()
            .Which.Should().Contain("author");
    }

    [Fact]
    public void ValidateDocumentMetadata_NoError_ForPresentRequiredField()
    {
        var store = CreateWithSchema([new MetadataField { Name = "author", Type = "str", Required = true }]);

        var errors = store.ValidateDocumentMetadata("test_col", "doc.pdf",
            new Dictionary<string, object?> { ["author"] = "Alice" });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateDocumentMetadata_ReturnsError_ForIntFieldWithStringValue()
    {
        var store = CreateWithSchema([new MetadataField { Name = "year", Type = "int" }]);

        var errors = store.ValidateDocumentMetadata("test_col", "doc.pdf",
            new Dictionary<string, object?> { ["year"] = "not-a-number" });

        errors.Should().ContainSingle().Which.Should().Contain("year");
    }

    [Fact]
    public void ValidateDocumentMetadata_NoError_ForIntFieldWithIntValue()
    {
        var store = CreateWithSchema([new MetadataField { Name = "year", Type = "int" }]);

        var errors = store.ValidateDocumentMetadata("test_col", "doc.pdf",
            new Dictionary<string, object?> { ["year"] = 2024 });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateDocumentMetadata_OptionalMissingField_IsNotAnError()
    {
        var store = CreateWithSchema([new MetadataField { Name = "tags", Type = "str", Required = false }]);

        var errors = store.ValidateDocumentMetadata("test_col", "doc.pdf", new Dictionary<string, object?>());

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateDocumentMetadata_ReturnsEmpty_ForUnknownCollection()
    {
        var store = new InMemoryIngestorStore();

        var errors = store.ValidateDocumentMetadata("nonexistent", "doc.pdf",
            new Dictionary<string, object?> { ["x"] = "y" });

        errors.Should().BeEmpty();
    }

    // ── Collection lifecycle ──────────────────────────────────────────────────

    [Fact]
    public void CreateCollection_ReturnsFalse_OnDuplicate()
    {
        var store = new InMemoryIngestorStore();
        store.CreateCollection(new CreateCollectionRequest { CollectionName = "col" });

        var second = store.CreateCollection(new CreateCollectionRequest { CollectionName = "col" });

        second.Should().BeFalse();
    }

    [Fact]
    public void UpsertDocuments_IgnoresDuplicate_WhenReplaceExistingFalse()
    {
        var store = new InMemoryIngestorStore();
        store.CreateCollection(new CreateCollectionRequest { CollectionName = "col" });

        var doc1 = new InMemoryIngestorStore.StoredDocument { DocumentName = "file.pdf", Metadata = new() { ["v"] = "1" } };
        var doc2 = new InMemoryIngestorStore.StoredDocument { DocumentName = "file.pdf", Metadata = new() { ["v"] = "2" } };

        store.UpsertDocuments("col", [doc1], replaceExisting: false);
        store.UpsertDocuments("col", [doc2], replaceExisting: false);

        store.GetDocumentCount("col").Should().Be(1);
    }
}
