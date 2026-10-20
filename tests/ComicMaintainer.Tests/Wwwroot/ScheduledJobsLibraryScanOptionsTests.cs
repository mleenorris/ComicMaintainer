using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// The Scheduled Jobs page renders a checkbox for each option listed for a job
/// in <c>SCHEDULED_JOB_OPTIONS</c> (js/app/scheduled-jobs.js). The
/// library-scan card must list every pass of LibraryScanJobHandler so each can
/// be switched on or off, with the same defaults as the handler's.
/// </summary>
public class ScheduledJobsLibraryScanOptionsTests
{
    private static string LibraryScanOptions()
    {
        var js = WwwrootFiles.Read("js/app/scheduled-jobs.js");
        var match = Regex.Match(js, @"'library-scan':\s*\[(?<options>[^\]]*)\]");
        Assert.True(match.Success, "SCHEDULED_JOB_OPTIONS must list the library-scan options.");
        return match.Groups["options"].Value;
    }

    private static string LibraryScanOption(string option)
    {
        var match = Regex.Match(LibraryScanOptions(), @"\{\s*name:\s*'" + option + @"'[^}]*\}");
        Assert.True(match.Success, $"The library-scan card must render the {option} option.");
        return match.Value;
    }

    [Theory]
    [InlineData("scanForNew")]
    [InlineData("reconcileDeletions")]
    [InlineData("retagStale")]
    public void JobsPage_RendersLibraryScanPass_OnByDefault(string option)
    {
        Assert.Contains("defaultOn: true", LibraryScanOption(option));
    }

    [Fact]
    public void JobsPage_ReprocessModified_IsOptIn()
    {
        // Unlike the other scan passes (default on), re-normalizing modified files is opt-in.
        Assert.DoesNotContain("defaultOn", LibraryScanOption("reprocessModified"));
    }

    [Fact]
    public void JobsPage_SavingKeepsOptionsTheCardDoesNotShow()
    {
        var js = WwwrootFiles.Read("js/app/scheduled-jobs.js");

        // Saving replaces the stored options JSON wholesale, so options without
        // a checkbox (e.g. metadata-backfill's maxBatch) must be carried over.
        Assert.Contains("JSON.stringify({ ...parseScheduledJobOptions(job), ...shownOptions })", js);
    }
}
