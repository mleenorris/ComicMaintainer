using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// REST endpoints for the scheduled-jobs framework. Lists registered jobs,
/// updates their schedule/options, triggers ad-hoc runs, and surfaces the
/// findings produced by the metadata-audit handler.
/// </summary>
[ApiController]
[Route("api/scheduled-jobs")]
[Authorize]
public class ScheduledJobsController : ControllerBase
{
    private readonly IScheduledJobService _service;
    private readonly ScheduledJobsHostedService _hostedService;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly ILogger<ScheduledJobsController> _logger;

    public ScheduledJobsController(
        IScheduledJobService service,
        ScheduledJobsHostedService hostedService,
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        ILogger<ScheduledJobsController> logger)
    {
        _service = service;
        _hostedService = hostedService;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> List(CancellationToken ct)
    {
        var views = await _service.ListAsync(ct);
        return Ok(views.Select(ToDto));
    }

    [HttpGet("{jobKey}")]
    public async Task<ActionResult<object>> Get(string jobKey, CancellationToken ct)
    {
        var view = await _service.GetAsync(jobKey, ct);
        if (view is null)
        {
            return NotFound();
        }
        return Ok(ToDto(view));
    }

    [HttpPut("{jobKey}")]
    public async Task<ActionResult<object>> Update(string jobKey, [FromBody] UpdateScheduledJobRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Body required." });
        }
        if (request.Enabled && request.IntervalMinutes <= 0)
        {
            return BadRequest(new { error = "intervalMinutes must be a positive number when enabled." });
        }
        try
        {
            var view = await _service.UpdateAsync(
                jobKey,
                request.Enabled,
                request.IntervalMinutes <= 0 ? 1 : request.IntervalMinutes,
                request.OptionsJson,
                ct);
            if (view is null)
            {
                return NotFound();
            }
            return Ok(ToDto(view));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{jobKey}/run")]
    public async Task<ActionResult<object>> Run(string jobKey, CancellationToken ct)
    {
        var view = await _service.GetAsync(jobKey, ct);
        if (view is null)
        {
            return NotFound();
        }
        var queued = await _hostedService.RunNowAsync(jobKey, ct);
        return Accepted(new { jobKey, queued });
    }

    [HttpGet("metadata-audit/findings")]
    public async Task<ActionResult<object>> GetMetadataAuditFindings(
        [FromQuery] string? type,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        if (offset < 0)
        {
            offset = 0;
        }
        if (limit <= 0 || limit > 1000)
        {
            limit = 200;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var query = db.MetadataAuditFindings.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(type))
        {
            query = query.Where(f => f.FindingType == type);
        }
        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(f => f.FilePath)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        return Ok(new
        {
            total,
            offset,
            limit,
            findings = rows.Select(r => new
            {
                id = r.Id,
                filePath = r.FilePath,
                findingType = r.FindingType,
                expectedSeries = r.ExpectedSeries,
                actualSeries = r.ActualSeries,
                actualIssue = r.ActualIssue,
                details = r.Details,
                detectedAtUtc = r.DetectedAtUtc,
            })
        });
    }

    private static object ToDto(ScheduledJobView view) => new
    {
        jobKey = view.JobKey,
        displayName = view.DisplayName,
        description = view.Description,
        enabled = view.Enabled,
        intervalMinutes = view.IntervalMinutes,
        lastRunUtc = view.LastRunUtc,
        nextRunUtc = view.NextRunUtc,
        lastDurationMs = view.LastDurationMs,
        lastStatus = view.LastStatus.ToString(),
        lastMessage = view.LastMessage,
        optionsJson = view.OptionsJson,
    };

    public class UpdateScheduledJobRequest
    {
        public bool Enabled { get; set; }
        public int IntervalMinutes { get; set; }
        public string? OptionsJson { get; set; }
    }
}
