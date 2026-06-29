using DotnetRag.Shared.Summarization;
using FluentAssertions;

namespace DotnetRag.Tests.Unit.Services;

public sealed class SummaryProgressTrackerTests
{
    [Fact]
    public void UpdateProgress_InProgress_PreservesProgressAndHasNoCompletedAt()
    {
        var tracker = new SummaryProgressTracker();

        tracker.UpdateProgress(
            "docs",
            "report.pdf",
            "IN_PROGRESS",
            new ProgressInfo(1, 3, "Processing chunk 1/3"));

        var progress = tracker.GetProgress("docs", "report.pdf");

        progress.Should().NotBeNull();
        progress!.Status.Should().Be("IN_PROGRESS");
        progress.Progress.Should().Be(new ProgressInfo(1, 3, "Processing chunk 1/3"));
        progress.StartedAt.Should().BeBefore(progress.UpdatedAt.AddMilliseconds(1));
        progress.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void UpdateProgress_Success_PreservesStartedAtAndSetsCompletedAt()
    {
        var tracker = new SummaryProgressTracker();
        tracker.UpdateProgress("docs", "report.pdf", "IN_PROGRESS");
        var startedAt = tracker.GetProgress("docs", "report.pdf")!.StartedAt;

        tracker.UpdateProgress("docs", "report.pdf", "SUCCESS");

        var progress = tracker.GetProgress("docs", "report.pdf");
        progress.Should().NotBeNull();
        progress!.Status.Should().Be("SUCCESS");
        progress.Error.Should().BeNull();
        progress.StartedAt.Should().Be(startedAt);
        progress.CompletedAt.Should().NotBeNull();
        progress.CompletedAt.Should().BeOnOrAfter(startedAt);
    }

    [Fact]
    public void UpdateProgress_Failed_StoresErrorAndSetsCompletedAt()
    {
        var tracker = new SummaryProgressTracker();

        tracker.UpdateProgress("docs", "report.pdf", "FAILED", error: "summary failed");

        var progress = tracker.GetProgress("docs", "report.pdf");
        progress.Should().NotBeNull();
        progress!.Status.Should().Be("FAILED");
        progress.Error.Should().Be("summary failed");
        progress.CompletedAt.Should().NotBeNull();
        progress.CompletedAt.Should().BeOnOrAfter(progress.StartedAt);
    }

    [Fact]
    public void FileSummaryProgressStore_PersistsAcrossTrackerInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"summary-progress-{Guid.NewGuid():N}.json");
        try
        {
            var first = new SummaryProgressTracker(new FileSummaryProgressStore(path));
            first.UpdateProgress(
                "docs",
                "report.pdf",
                "IN_PROGRESS",
                new ProgressInfo(1, 2, "Processing chunk 1/2"));
            var startedAt = first.GetProgress("docs", "report.pdf")!.StartedAt;

            var second = new SummaryProgressTracker(new FileSummaryProgressStore(path));
            second.GetProgress("docs", "report.pdf")!.StartedAt.Should().Be(startedAt);

            second.UpdateProgress("docs", "report.pdf", "SUCCESS");

            var third = new SummaryProgressTracker(new FileSummaryProgressStore(path));
            var progress = third.GetProgress("docs", "report.pdf");
            progress.Should().NotBeNull();
            progress!.Status.Should().Be("SUCCESS");
            progress.StartedAt.Should().Be(startedAt);
            progress.CompletedAt.Should().NotBeNull();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
