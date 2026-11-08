using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

public partial class FilesController
{
    /// <summary>
    /// Plans a folder combine without changing anything: the file moves, and
    /// which source folders will be removed afterwards or kept (with what
    /// keeps them).
    /// </summary>
    [HttpPost("combine-folders/preview")]
    [FailureMessage("Error building combine preview")]
    public async Task<ActionResult<object>> PreviewCombineFolders(
        [FromBody] CombineFoldersRequest request,
        CancellationToken cancellationToken = default)
    {
        var (error, plan) = await _folderCombine.PreviewAsync(ToFolderCombineRequest(request), cancellationToken);
        if (error != null)
        {
            return BadRequest(new { error });
        }

        return Ok(new
        {
            destination = plan!.Destination,
            sources = plan.SourceDirectories,
            moves = plan.Moves.Select(m => new
            {
                sourcePath = m.SourcePath,
                destinationPath = m.DestinationPath,
                conflict = m.HadConflict,
                skipped = m.Skipped,
                reason = m.Reason
            }),
            foldersToRemove = plan.SourceCleanup
                .Where(c => c.Removable)
                .Select(c => new
                {
                    directory = c.Directory,
                    disposableFiles = c.DisposableFiles,
                    disposableFileCount = c.DisposableFileCount
                })
                .ToList(),
            foldersToKeep = plan.SourceCleanup
                .Where(c => !c.Removable)
                .Select(c => new
                {
                    directory = c.Directory,
                    reason = c.KeptReason,
                    leftovers = c.Leftovers,
                    leftoverCount = c.LeftoverCount
                })
                .ToList()
        });
    }

    /// <summary>
    /// Starts a folder combine as a background job and returns its id at once;
    /// progress arrives through the usual job events. Source folders that
    /// still hold anything but disposable files are kept and listed in the
    /// job status's <c>reported_problems</c>.
    /// </summary>
    [HttpPost("combine-folders")]
    [FailureMessage("Error combining folders")]
    public async Task<ActionResult<object>> CombineFolders(
        [FromBody] CombineFoldersRequest request,
        CancellationToken cancellationToken = default)
    {
        var (error, plan, jobId) = await _folderCombine.StartAsync(ToFolderCombineRequest(request), cancellationToken);
        if (error != null)
        {
            return BadRequest(new { error });
        }

        // Skipped entries are decided synchronously when the plan was built;
        // they need no work and can be reported in the immediate response.
        var skippedResults = plan!.Moves
            .Where(m => m.Skipped)
            .Select(m => new { sourcePath = m.SourcePath, destinationPath = m.DestinationPath, status = "skipped", reason = m.Reason })
            .ToList();

        // Nothing to move — return immediately with the skipped results so
        // the UI doesn't have to track an empty job.
        if (jobId is null)
        {
            return Accepted(new
            {
                destination = plan.Destination,
                sources = plan.SourceDirectories,
                moved = 0,
                skipped = skippedResults.Count,
                failed = 0,
                totalItems = 0,
                results = skippedResults
            });
        }

        var totalItems = plan.Moves.Count(m => !m.Skipped);

        // Return immediately with the job id and the plan summary so the UI
        // can attach its progress modal via the standard SSE pipeline. The
        // actual moved/failed counts come from job_updated events.
        return Accepted(new
        {
            jobId = jobId.Value.ToString(),
            job_id = jobId.Value.ToString(),
            totalItems,
            total_items = totalItems,
            destination = plan.Destination,
            sources = plan.SourceDirectories,
            skipped = skippedResults.Count,
            skippedResults
        });
    }

    /// <summary>
    /// Maps the request body to the service's request; a missing body stays
    /// null so the service reports it like any other invalid request.
    /// </summary>
    private static FolderCombineRequest? ToFolderCombineRequest(CombineFoldersRequest? request) =>
        request is null
            ? null
            : new FolderCombineRequest
            {
                GroupKey = request.GroupKey,
                DestinationDirectory = request.DestinationDirectory,
                SourceDirectories = request.SourceDirectories
            };

    public class CombineFoldersRequest
    {
        public string? GroupKey { get; set; }
        public string DestinationDirectory { get; set; } = string.Empty;
        public List<string>? SourceDirectories { get; set; }
    }
}
