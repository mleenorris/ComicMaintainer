using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Scheduled job that imports finished Suwayomi downloads into the library
/// (see <see cref="SuwayomiDownloadImportService"/>). Configuration problems
/// are reported in the run summary instead of failing the run, so a missing
/// mount does not raise an error report every few minutes.
/// </summary>
public class SuwayomiDownloadImportJobHandler : IScheduledJobHandler
{
    public const string Key = "suwayomi-download-import";

    // Cap on how many series are named in the last-run summary.
    private const int MaxSeriesNamedInSummary = 10;

    private readonly ISuwayomiDownloadImportService _importer;

    public SuwayomiDownloadImportJobHandler(ISuwayomiDownloadImportService importer)
    {
        _importer = importer;
    }

    public string JobKey => Key;
    public string DisplayName => "Suwayomi Download Import";
    public string Description =>
        "Moves chapters Suwayomi has finished downloading (saved as source/series/chapter.cbz) into the library as series/chapter.cbz, joining the series' existing folder when there is one, then removes the emptied download folders. Set the folders under Settings → Suwayomi; until a downloads folder is set, the job does nothing.";

    // Enabled by default so setting the downloads folder is all it takes; a
    // run without one is a cheap no-op.
    public ScheduledJobDefaults Defaults => new(Enabled: true, IntervalMinutes: 15);

    public async Task<string> ExecuteAsync(string? optionsJson, CancellationToken cancellationToken)
    {
        var result = await _importer.ImportAsync(cancellationToken);
        return FormatSummary(result);
    }

    public static string FormatSummary(SuwayomiImportResult result)
    {
        if (result.NotConfigured)
        {
            return "Skipped: no Suwayomi downloads folder is configured (Settings → Suwayomi).";
        }

        if (result.SkippedReason is not null)
        {
            return $"Skipped: {result.SkippedReason}";
        }

        var parts = new List<string>();
        if (result.Imported.Count > 0)
        {
            var series = result.Imported
                .Select(chapter => Path.GetDirectoryName(chapter.DestinationPath))
                .Where(directory => !string.IsNullOrEmpty(directory))
                .Distinct(StringComparer.Ordinal)
                .Select(directory => Path.GetFileName(directory))
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();
            var named = string.Join(", ", series.Take(MaxSeriesNamedInSummary));
            if (series.Count > MaxSeriesNamedInSummary)
            {
                named += $", +{series.Count - MaxSeriesNamedInSummary} more";
            }

            parts.Add($"imported {result.Imported.Count} chapter(s) into {series.Count} series folder(s) ({named})");
        }

        if (result.PermissionDenied)
        {
            parts.Add("stopped: permission denied reading or moving a chapter (ComicMaintainer's user needs write access to the Suwayomi downloads folder and the library)");
        }

        if (result.StillDownloading > 0)
        {
            parts.Add($"{result.StillDownloading} still downloading");
        }

        if (result.Unreadable > 0)
        {
            parts.Add($"{result.Unreadable} unreadable archive(s) left in place");
        }

        var otherFailures = result.Failed - (result.PermissionDenied ? 1 : 0);
        if (otherFailures > 0)
        {
            parts.Add($"{otherFailures} could not be moved (see the log)");
        }

        if (result.WithoutSeriesFolder > 0)
        {
            parts.Add($"{result.WithoutSeriesFolder} left in place for lack of a series folder");
        }

        if (result.RemovedFolders > 0)
        {
            parts.Add($"removed {result.RemovedFolders} empty folder(s)");
        }

        var joined = string.Join("; ", parts);
        var summary = parts.Count == 0
            ? "No new Suwayomi downloads."
            : char.ToUpperInvariant(joined[0]) + joined[1..] + ".";

        if (result.ImageFolders > 0)
        {
            summary += $" {result.ImageFolders} chapter folder(s) hold loose images and were not imported: enable \"Save as CBZ archive\" in Suwayomi's download settings.";
        }

        return summary;
    }
}
