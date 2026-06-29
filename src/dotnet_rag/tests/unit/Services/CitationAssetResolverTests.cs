using DotnetRag.Rag.Services;
using DotnetRag.Shared.Abstractions;
using DotnetRag.Shared.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetRag.Tests.Unit.Services;

public sealed class CitationAssetResolverTests
{
    [Fact]
    public async Task ResolveAsync_WithS3UriUnderObjectStoreRoot_ReturnsBase64Asset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dotnet-rag-citations-{Guid.NewGuid():N}");
        var assetPath = Path.Combine(root, "assets", "charts", "revenue.png");
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        await File.WriteAllBytesAsync(assetPath, [1, 2, 3, 4]);

        try
        {
            var resolver = new FileSystemCitationAssetResolver(
                new RagServerConfiguration { ObjectStoreRoot = root },
                NullLogger<FileSystemCitationAssetResolver>.Instance);
            var result = new VectorSearchResult(
                "image-1",
                "Revenue chart",
                0.9,
                new Dictionary<string, string>
                {
                    ["type"] = "image",
                    ["stored_image_uri"] = "s3://assets/charts/revenue.png"
                });

            var asset = await resolver.ResolveAsync(result);

            asset.Should().NotBeNull();
            asset!.ContentBase64.Should().Be("AQIDBA==");
            asset.DocumentType.Should().Be("image");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResolveAsync_WithAssetObjectNamesJsonArray_ReturnsFirstResolvableAsset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dotnet-rag-citations-{Guid.NewGuid():N}");
        var assetPath = Path.Combine(root, "visuals", "page-2.png");
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        await File.WriteAllBytesAsync(assetPath, [5, 6, 7]);

        try
        {
            var resolver = new FileSystemCitationAssetResolver(
                new RagServerConfiguration { ObjectStoreRoot = root },
                NullLogger<FileSystemCitationAssetResolver>.Instance);
            var result = new VectorSearchResult(
                "image-1",
                "Page visual",
                0.9,
                new Dictionary<string, string>
                {
                    ["content_metadata.type"] = "image",
                    ["asset_object_names"] = """["visuals/page-2.png","visuals/page-3.png"]"""
                });

            var asset = await resolver.ResolveAsync(result);

            asset.Should().NotBeNull();
            asset!.ContentBase64.Should().Be("BQYH");
            asset.DocumentType.Should().Be("image");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResolveAsync_WithAssetObjectNamesDelimitedString_ReturnsFirstResolvableAsset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dotnet-rag-citations-{Guid.NewGuid():N}");
        var assetPath = Path.Combine(root, "visuals", "table-1.png");
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        await File.WriteAllBytesAsync(assetPath, [8, 9]);

        try
        {
            var resolver = new FileSystemCitationAssetResolver(
                new RagServerConfiguration { ObjectStoreRoot = root },
                NullLogger<FileSystemCitationAssetResolver>.Instance);
            var result = new VectorSearchResult(
                "table-1",
                "Table visual",
                0.9,
                new Dictionary<string, string>
                {
                    ["type"] = "structured",
                    ["content_metadata.subtype"] = "table",
                    ["asset_object_names"] = "visuals/table-1.png; visuals/table-2.png"
                });

            var asset = await resolver.ResolveAsync(result);

            asset.Should().NotBeNull();
            asset!.ContentBase64.Should().Be("CAk=");
            asset.DocumentType.Should().Be("table");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResolveAsync_WithFlattenedSourceLocation_ReturnsBase64Asset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dotnet-rag-citations-{Guid.NewGuid():N}");
        var assetPath = Path.Combine(root, "assets", "page-4.png");
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        await File.WriteAllBytesAsync(assetPath, [10, 11]);

        try
        {
            var resolver = new FileSystemCitationAssetResolver(
                new RagServerConfiguration { ObjectStoreRoot = root },
                NullLogger<FileSystemCitationAssetResolver>.Instance);
            var result = new VectorSearchResult(
                "image-1",
                "Page visual",
                0.9,
                new Dictionary<string, string>
                {
                    ["content_metadata.type"] = "image",
                    ["source.source_location"] = "assets/page-4.png"
                });

            var asset = await resolver.ResolveAsync(result);

            asset.Should().NotBeNull();
            asset!.ContentBase64.Should().Be("Cgs=");
            asset.DocumentType.Should().Be("image");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResolveAsync_WithThumbnailObjectName_ReturnsBase64Asset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dotnet-rag-citations-{Guid.NewGuid():N}");
        var assetPath = Path.Combine(root, "collection_::_file.pdf_::_1_0_0_1_1");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(assetPath, [12, 13]);

        try
        {
            var resolver = new FileSystemCitationAssetResolver(
                new RagServerConfiguration { ObjectStoreRoot = root },
                NullLogger<FileSystemCitationAssetResolver>.Instance);
            var result = new VectorSearchResult(
                "thumb-1",
                "Generated thumbnail",
                0.9,
                new Dictionary<string, string>
                {
                    ["type"] = "image",
                    ["thumbnail_object_name"] = "collection_::_file.pdf_::_1_0_0_1_1"
                });

            var asset = await resolver.ResolveAsync(result);

            asset.Should().NotBeNull();
            asset!.ContentBase64.Should().Be("DA0=");
            asset.DocumentType.Should().Be("image");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResolveAsync_WithNestedSourceAndContentMetadata_ReturnsTypedAsset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dotnet-rag-citations-{Guid.NewGuid():N}");
        var assetPath = Path.Combine(root, "visuals", "table-7.png");
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        await File.WriteAllBytesAsync(assetPath, [14, 15]);

        try
        {
            var resolver = new FileSystemCitationAssetResolver(
                new RagServerConfiguration { ObjectStoreRoot = root },
                NullLogger<FileSystemCitationAssetResolver>.Instance);
            var result = new VectorSearchResult(
                "table-7",
                "Extracted table",
                0.9,
                new Dictionary<string, string>
                {
                    ["source"] = """{"source_location":"visuals/table-7.png"}""",
                    ["content_metadata"] = """{"type":"structured","subtype":"table","page_number":7}"""
                });

            var asset = await resolver.ResolveAsync(result);

            asset.Should().NotBeNull();
            asset!.ContentBase64.Should().Be("Dg8=");
            asset.DocumentType.Should().Be("table");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResolveAsync_WithAssetObjectNamesObjectArray_ReturnsFirstObjectAsset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dotnet-rag-citations-{Guid.NewGuid():N}");
        var assetPath = Path.Combine(root, "visuals", "chart-2.png");
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        await File.WriteAllBytesAsync(assetPath, [16, 17]);

        try
        {
            var resolver = new FileSystemCitationAssetResolver(
                new RagServerConfiguration { ObjectStoreRoot = root },
                NullLogger<FileSystemCitationAssetResolver>.Instance);
            var result = new VectorSearchResult(
                "chart-2",
                "Extracted chart",
                0.9,
                new Dictionary<string, string>
                {
                    ["content_metadata.type"] = "structured",
                    ["content_metadata.subtype"] = "chart",
                    ["asset_object_names"] = """[{"object_name":"visuals/chart-2.png","type":"chart"}]"""
                });

            var asset = await resolver.ResolveAsync(result);

            asset.Should().NotBeNull();
            asset!.ContentBase64.Should().Be("EBE=");
            asset.DocumentType.Should().Be("chart");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResolveAsync_WithInlineDataUri_ReturnsEmbeddedBase64Asset()
    {
        var resolver = new FileSystemCitationAssetResolver(
            new RagServerConfiguration(),
            NullLogger<FileSystemCitationAssetResolver>.Instance);
        var result = new VectorSearchResult(
            "inline-image",
            "Inline image",
            0.9,
            new Dictionary<string, string>
            {
                ["type"] = "image",
                ["image_uri"] = "data:image/png;base64,SGVsbG8="
            });

        var asset = await resolver.ResolveAsync(result);

        asset.Should().NotBeNull();
        asset!.ContentBase64.Should().Be("SGVsbG8=");
        asset.DocumentType.Should().Be("image");
    }

    [Fact]
    public async Task ResolveAsync_WithTextCitation_ReturnsNull()
    {
        var resolver = new FileSystemCitationAssetResolver(
            new RagServerConfiguration(),
            NullLogger<FileSystemCitationAssetResolver>.Instance);
        var result = new VectorSearchResult(
            "text-1",
            "Text citation",
            0.9,
            new Dictionary<string, string>
            {
                ["type"] = "text",
                ["stored_image_uri"] = "s3://assets/charts/revenue.png"
            });

        var asset = await resolver.ResolveAsync(result);

        asset.Should().BeNull();
    }
}
