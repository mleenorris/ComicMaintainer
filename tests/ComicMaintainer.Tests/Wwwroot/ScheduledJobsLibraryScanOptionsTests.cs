namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// The scheduled-jobs page saves a job's options by serializing every
/// <c>data-option</c> input on its card, replacing the stored options JSON.
/// The library-scan card must therefore render all of the handler's options,
/// otherwise saving it would silently drop the ones that aren't shown.
/// </summary>
public class ScheduledJobsLibraryScanOptionsTests
{
    [Theory]
    [InlineData("scanForNew")]
    [InlineData("reconcileDeletions")]
    [InlineData("retagStale")]
    [InlineData("reprocessModified")]
    public void JobsPage_RendersLibraryScanOption(string option)
    {
        var content = WwwrootFiles.Read("jobs.html");

        Assert.Contains("job.jobKey === 'library-scan'", content);
        Assert.Contains($"data-option=\"{option}\"", content);
    }

    [Fact]
    public void JobsPage_ReprocessModified_IsOptIn()
    {
        var content = WwwrootFiles.Read("jobs.html");

        // Unlike the other scan passes (default on), re-normalizing modified files is opt-in.
        Assert.Contains("data-option=\"reprocessModified\" ${opts.reprocessModified ? 'checked' : ''}", content);
    }
}
