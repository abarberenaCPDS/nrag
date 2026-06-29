using System.Text.Json;
using FluentAssertions;

namespace DotnetRag.Tests.Unit;

public sealed class NotebookContractTests
{
    [Fact]
    public void DotnetQuickstartNotebook_UsesUnversionedDotnetRoutes()
    {
        var repoRoot = FindRepoRoot();
        var notebookPath = Path.Combine(repoRoot, "deploy", "workbench", "quickstart-dotnet.ipynb");
        File.Exists(notebookPath).Should().BeTrue();

        var content = File.ReadAllText(notebookPath);
        using var _ = JsonDocument.Parse(content);

        content.Should().NotContain("/" + "v1");
        content.Should().NotContain("/" + "v2");
        content.Should().Contain("/health");
        content.Should().Contain("/configuration");
        content.Should().Contain("/collections");
        content.Should().Contain("/documents");
        content.Should().Contain("/chat/completions");
        content.Should().Contain("/search");
        content.Should().Contain("/summary");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "dotnet_rag", "DotnetRag.sln");
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
