using System.Text.Json;
using DotnetRag.Ingestor.Models;
using DotnetRag.Ingestor.Services;
using DotnetRag.Shared.Summarization;
using FluentAssertions;

namespace DotnetRag.Tests.Unit.Services;

public sealed class SummaryOptionsValidatorTests
{
    [Fact]
    public void ValidateAndNormalize_RejectsOptions_WhenSummaryDisabled()
    {
        var options = new SummaryOptions { PageFilter = "even" };

        var error = SummaryOptionsValidator.ValidateAndNormalize(false, options);

        error.Should().Contain("summary_options can only be provided");
    }

    [Fact]
    public void ValidateAndNormalize_NormalizesParityPageFilter()
    {
        var options = new SummaryOptions { PageFilter = "EVEN" };

        var error = SummaryOptionsValidator.ValidateAndNormalize(true, options);

        error.Should().BeNull();
        options.PageFilter.Should().Be("even");
    }

    [Theory]
    [InlineData("all")]
    [InlineData("")]
    public void ValidateAndNormalize_RejectsUnsupportedParityPageFilter(string pageFilter)
    {
        var options = new SummaryOptions { PageFilter = pageFilter };

        var error = SummaryOptionsValidator.ValidateAndNormalize(true, options);

        error.Should().Contain("Invalid page_filter string");
    }

    [Fact]
    public void ValidateAndNormalize_RejectsEmptyRangeList()
    {
        var options = new SummaryOptions { PageFilter = new List<List<int>>() };

        var error = SummaryOptionsValidator.ValidateAndNormalize(true, options);

        error.Should().Be("Page filter range list cannot be empty");
    }

    [Theory]
    [InlineData("[[0,1]]", "page numbers cannot be 0")]
    [InlineData("[[5,1]]", "start must be <= end")]
    [InlineData("[[-1,-10]]", "invalid negative range")]
    [InlineData("[[1,-1]]", "cannot mix positive and negative")]
    [InlineData("[[1,2,3]]", "exactly 2 elements")]
    [InlineData("[[1,\"2\"]]", "must contain integers")]
    [InlineData("[1,2]", "must contain ranges")]
    public void ValidateAndNormalize_RejectsInvalidJsonPageFilter(string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);
        var options = new SummaryOptions { PageFilter = doc.RootElement.Clone() };

        var error = SummaryOptionsValidator.ValidateAndNormalize(true, options);

        error.Should().Contain(expected);
    }

    [Theory]
    [InlineData("single")]
    [InlineData("hierarchical")]
    public void ValidateAndNormalize_AcceptsPythonSupportedStrategies(string strategy)
    {
        var options = new SummaryOptions { SummarizationStrategy = strategy };

        var error = SummaryOptionsValidator.ValidateAndNormalize(true, options);

        error.Should().BeNull();
    }

    [Theory]
    [InlineData("iterative")]
    [InlineData("Single")]
    public void ValidateAndNormalize_RejectsUnsupportedStrategies(string strategy)
    {
        var options = new SummaryOptions { SummarizationStrategy = strategy };

        var error = SummaryOptionsValidator.ValidateAndNormalize(true, options);

        error.Should().Contain("Invalid summarization_strategy");
    }

    [Fact]
    public void PageFilter_MatchesInMemoryRangeLists()
    {
        var filter = new List<List<int>> { new() { -2, -1 } };

        PageFilter.Matches(4, filter, totalPages: 5).Should().BeTrue();
        PageFilter.Matches(2, filter, totalPages: 5).Should().BeFalse();
    }
}
