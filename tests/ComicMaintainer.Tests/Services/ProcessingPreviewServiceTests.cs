using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// <see cref="ProcessingPreviewService"/>: a preview runs as a batch job, its results are read in
/// filtered, sorted pages with a summary, and files of one preview that would get the same name
/// are flagged.
/// </summary>
public sealed class ProcessingPreviewServiceTests : IDisposable
{
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly BatchJobRegistry _jobs = TestServices.CreateBatchJobRegistry(
        new TestOptionsMonitor<AppSettings>(new AppSettings { MaxWorkers = 1 }),
        NullLogger<BatchJobRegistry>.Instance);

    private readonly Dictionary<string, ProcessingPreviewItem> _previews = new(StringComparer.Ordinal);

    public ProcessingPreviewServiceTests()
    {
        _processor.Setup(p => p.PreviewProcessingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) => _previews.TryGetValue(path, out var item)
                ? item
                : new ProcessingPreviewItem { FilePath = path, Series = "Unchanged" });
    }

    public void Dispose() => _jobs.Dispose();

    private ProcessingPreviewService CreateService(IBatchJobRegistry? jobs = null) =>
        new(_processor.Object, jobs ?? _jobs, NullLogger<ProcessingPreviewService>.Instance);

    private void Preview(ProcessingPreviewItem item) => _previews[item.FilePath] = item;

    private async Task<Guid> RunAsync(ProcessingPreviewService service, params string[] files)
    {
        var jobId = service.StartPreview(files);
        var job = await _jobs.WaitForCompletionAsync(jobId).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(JobStatus.Completed, job!.Status);
        return jobId;
    }

    [Fact]
    public async Task GetResults_ListsTheChangedFilesBySeries_WithASummaryOfEveryFile()
    {
        Preview(new ProcessingPreviewItem { FilePath = "/w/Saga/b.cbz", Series = "Saga", NewFilePath = "/w/Saga/Saga 2.cbz" });
        Preview(new ProcessingPreviewItem { FilePath = "/w/Saga/a.cbz", Series = "Saga", TagChanges = [new ComicInfoFieldChange("Series", "saga", "Saga")] });
        Preview(new ProcessingPreviewItem { FilePath = "/w/Batman/x.cbz", Series = "Batman", Error = "File not found" });
        Preview(new ProcessingPreviewItem { FilePath = "/w/Batman/y.cbz", Series = "batman", NewFilePath = "/w/Batman/Taken.cbz", Clash = ProcessingPreviewClash.ExistingFile, ClashWith = "/w/Batman/Taken.cbz" });
        Preview(new ProcessingPreviewItem { FilePath = "/w/Batman/z.cbr", Series = "Batman", Warning = "not written" });
        var service = CreateService();

        var jobId = await RunAsync(service, "/w/Saga/b.cbz", "/w/Saga/a.cbz", "/w/Batman/x.cbz", "/w/Batman/y.cbz", "/w/Batman/z.cbr", "/w/Same/c.cbz", " ", "/w/Saga/a.cbz");
        var results = service.GetResults(jobId, ProcessingPreviewFilter.Changes, 0, 100)!;

        Assert.Equal("completed", results.Status);
        Assert.True(results.Finished);
        Assert.Equal(6, results.TotalFiles);
        Assert.Equal(new ProcessingPreviewSummary(Checked: 6, Changed: 5, Renamed: 1, TagsChanged: 1, Clashes: 1, Unchanged: 1, Errors: 1), results.Summary);
        Assert.Equal(5, results.Total);
        Assert.Equal(
            new[] { "/w/Batman/x.cbz", "/w/Batman/y.cbz", "/w/Batman/z.cbr", "/w/Saga/a.cbz", "/w/Saga/b.cbz" },
            results.Items.Select(item => item.FilePath));

        // Each filter, and every file.
        Assert.Equal(6, service.GetResults(jobId, ProcessingPreviewFilter.All, 0, 100)!.Total);
        Assert.Equal("/w/Saga/b.cbz", Assert.Single(service.GetResults(jobId, ProcessingPreviewFilter.Renamed, 0, 100)!.Items).FilePath);
        Assert.Equal("/w/Saga/a.cbz", Assert.Single(service.GetResults(jobId, ProcessingPreviewFilter.Tags, 0, 100)!.Items).FilePath);
        Assert.Equal("/w/Batman/y.cbz", Assert.Single(service.GetResults(jobId, ProcessingPreviewFilter.Clashes, 0, 100)!.Items).FilePath);
        Assert.Equal("/w/Batman/x.cbz", Assert.Single(service.GetResults(jobId, ProcessingPreviewFilter.Errors, 0, 100)!.Items).FilePath);
        _processor.Verify(p => p.PreviewProcessingAsync("/w/Saga/a.cbz", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetResults_Pages()
    {
        foreach (var name in new[] { "a", "b", "c" })
        {
            Preview(new ProcessingPreviewItem { FilePath = $"/w/S/{name}.cbz", Series = "S", Warning = "w" });
        }

        var service = CreateService();
        var jobId = await RunAsync(service, "/w/S/c.cbz", "/w/S/b.cbz", "/w/S/a.cbz");

        var page = service.GetResults(jobId, ProcessingPreviewFilter.Changes, 1, 1)!;
        Assert.Equal(3, page.Total);
        Assert.Equal(1, page.Offset);
        Assert.Equal("/w/S/b.cbz", Assert.Single(page.Items).FilePath);

        // Past the end, and a nonsense offset and limit.
        Assert.Empty(service.GetResults(jobId, ProcessingPreviewFilter.Changes, 10, 5)!.Items);
        var clamped = service.GetResults(jobId, ProcessingPreviewFilter.Changes, -4, 0)!;
        Assert.Equal(0, clamped.Offset);
        Assert.Equal(1, clamped.Limit);
    }

    [Fact]
    public async Task FilesGettingTheSameNewName_AllButTheFirstAreFlagged()
    {
        Preview(new ProcessingPreviewItem { FilePath = "/w/S/one.cbz", Series = "S", NewFilePath = "/w/S/S - Chapter 0001.cbz" });
        Preview(new ProcessingPreviewItem { FilePath = "/w/S/two.cbz", Series = "S", NewFilePath = "/w/S/s - chapter 0001.cbz" });
        Preview(new ProcessingPreviewItem { FilePath = "/w/S/three.cbz", Series = "S", NewFilePath = "/w/S/S - Chapter 0003.cbz" });
        var service = CreateService();

        var jobId = await RunAsync(service, "/w/S/two.cbz", "/w/S/one.cbz", "/w/S/three.cbz");
        var items = service.GetResults(jobId, ProcessingPreviewFilter.All, 0, 100)!.Items.ToDictionary(item => item.FilePath);

        Assert.Equal(ProcessingPreviewClash.None, items["/w/S/two.cbz"].Clash);
        Assert.Equal(ProcessingPreviewClash.SameTargetInPreview, items["/w/S/one.cbz"].Clash);
        Assert.Equal("/w/S/two.cbz", items["/w/S/one.cbz"].ClashWith);
        Assert.False(items["/w/S/one.cbz"].Renames);
        Assert.Equal(ProcessingPreviewClash.None, items["/w/S/three.cbz"].Clash);
        Assert.Equal(1, service.GetResults(jobId, ProcessingPreviewFilter.Changes, 0, 100)!.Summary.Clashes);
    }

    [Fact]
    public void GetResults_OfAnUnknownPreview_IsNull()
    {
        Assert.Null(CreateService().GetResults(Guid.NewGuid(), ProcessingPreviewFilter.Changes, 0, 10));
        Assert.Throws<ArgumentNullException>(() => CreateService().StartPreview(null!));
    }

    [Fact]
    public async Task OnlyTheNewestPreviewsAreKept()
    {
        var service = CreateService();
        var ids = new List<Guid>();
        for (var i = 0; i <= ProcessingPreviewService.KeepMostRecent; i++)
        {
            ids.Add(await RunAsync(service, $"/w/S/{i}.cbz"));
        }

        Assert.Null(service.GetResults(ids[0], ProcessingPreviewFilter.All, 0, 10));
        Assert.All(ids.Skip(1), id => Assert.NotNull(service.GetResults(id, ProcessingPreviewFilter.All, 0, 10)));
    }

    [Fact]
    public void APreviewWhoseJobIsUnknownOrNeverFinished_ReadsAsInterrupted()
    {
        var jobs = new Mock<IBatchJobRegistry>();
        var jobId = Guid.NewGuid();
        jobs.Setup(j => j.StartJob(It.IsAny<BatchJobDefinition>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())).Returns(jobId);
        jobs.Setup(j => j.GetJobSummaries()).Returns([]);
        var service = CreateService(jobs.Object);

        Assert.Equal(jobId, service.StartPreview(["/w/S/a.cbz"]));
        var results = service.GetResults(jobId, ProcessingPreviewFilter.All, 0, 10)!;

        Assert.Equal("interrupted", results.Status);
        Assert.False(results.Finished);
        Assert.Equal(1, results.TotalFiles);
        Assert.Equal(0, results.Summary.Checked);
    }

    [Fact]
    public async Task AFinishedPreviewWhoseJobWasForgotten_ReadsAsCompleted()
    {
        Preview(new ProcessingPreviewItem { FilePath = "/w/S/a.cbz", Series = "S", Warning = "w" });
        var service = CreateService();
        var jobId = await RunAsync(service, "/w/S/a.cbz");

        Assert.True(await _jobs.DeleteJobAsync(jobId));
        var results = service.GetResults(jobId, ProcessingPreviewFilter.Changes, 0, 10)!;

        Assert.Equal("completed", results.Status);
        Assert.Single(results.Items);
    }

    [Fact]
    public void MarkSharedTargets_LeavesFilesThatAreNotRenamedAlone()
    {
        var order = new Dictionary<string, int> { ["/a"] = 0, ["/b"] = 1, ["/c"] = 2 };
        ProcessingPreviewItem[] items =
        [
            new() { FilePath = "/a", NewFilePath = "/t", Error = "failed" },
            new() { FilePath = "/b", NewFilePath = "/t" },
            new() { FilePath = "/c" }
        ];

        var marked = ProcessingPreviewService.MarkSharedTargets(items, order);

        Assert.All(marked, item => Assert.Equal(ProcessingPreviewClash.None, item.Clash));
    }
}
