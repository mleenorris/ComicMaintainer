using System.Collections.Concurrent;
using System.Text.Json;
using ComicMaintainer.Core.Interfaces;
using Microsoft.AspNetCore.SignalR;
using ComicMaintainer.WebApi.Hubs;

namespace ComicMaintainer.WebApi.Services;

/// <summary>
/// Service for broadcasting events to connected SSE clients
/// (<c>GET /api/events/stream</c>) and SignalR hubs (<see cref="ProgressHub"/>).
///
/// <para>
/// SSE event contract — every event is a JSON object with a <c>type</c>
/// string and a <c>data</c> object payload:
/// </para>
/// <list type="bullet">
///   <item><description><c>job_updated</c> — emitted by
///     <see cref="BroadcastJobUpdateAsync"/> for batch-processing jobs
///     (process / rename / normalize / metadata update / remove-metadata /
///     bulk-delete / combine-folders / series-cover embed). Payload includes
///     <c>job_id</c>, <c>status</c>, and a <c>progress</c> sub-object with
///     <c>processed</c>, <c>total</c>, <c>success</c>, <c>errors</c>,
///     <c>percentage</c>.</description></item>
///   <item><description><c>file_processed</c> — emitted by
///     <see cref="BroadcastFileProcessedAsync"/> after each file in a batch
///     job (and after one-off metadata edits, tag writes, and
///     remove-embedded-metadata). The UI uses this to invalidate a single
///     folder's cache and patch the visible series-detail / file row in
///     place without rebuilding the whole library.</description></item>
///   <item><description><c>file_list_updated</c> — emitted by
///     <see cref="BroadcastFileListUpdateAsync"/> when files are added or
///     removed from the file store, or after a coalesced batch of moves
///     (e.g. combine-folders). UI debounces this to a single library
///     refresh.</description></item>
///   <item><description><c>watcher_status</c> — emitted by
///     <see cref="BroadcastWatcherStatusAsync"/> when the file-system
///     watcher starts/stops or is toggled.</description></item>
/// </list>
///
/// <para>
/// All broadcasts are best-effort and never throw to the caller. A failing
/// SSE writer is silently disconnected and removed from the registry.
/// </para>
/// </summary>
public class EventBroadcasterService : IEventBroadcaster
{
    private readonly ILogger<EventBroadcasterService> _logger;
    private readonly IHubContext<ProgressHub> _hubContext;
    private readonly ConcurrentDictionary<string, StreamWriter> _sseClients = new();

    public EventBroadcasterService(
        ILogger<EventBroadcasterService> logger,
        IHubContext<ProgressHub> hubContext)
    {
        _logger = logger;
        _hubContext = hubContext;
    }

    public void RegisterSseClient(string clientId, StreamWriter writer)
    {
        _sseClients[clientId] = writer;
        _logger.LogDebug("SSE client registered: {ClientId}", clientId);
    }

    public void UnregisterSseClient(string clientId)
    {
        _sseClients.TryRemove(clientId, out _);
        _logger.LogDebug("SSE client unregistered: {ClientId}", clientId);
    }

    public async Task BroadcastJobUpdateAsync(Guid jobId, string status, int processed, int total, int success, int errors)
    {
        var eventData = new
        {
            type = "job_updated",
            data = new
            {
                job_id = jobId.ToString(),
                status = status,
                progress = new
                {
                    processed = processed,
                    total = total,
                    success = success,
                    errors = errors,
                    percentage = total > 0 ? (processed * 100 / total) : 0
                }
            }
        };

        await BroadcastEventAsync(eventData);
        
        // Also broadcast via SignalR to job-specific group
        await _hubContext.Clients.Group($"job-{jobId}").SendAsync("JobUpdate", eventData.data);
        
        _logger.LogDebug("Broadcasted job update: {JobId} - {Status} ({Processed}/{Total})", 
            jobId, status, processed, total);
    }

    public async Task BroadcastFileProcessedAsync(string filename, bool success, string? error = null)
    {
        var eventData = new
        {
            type = "file_processed",
            data = new
            {
                filename = filename,
                success = success,
                error = error
            }
        };

        await BroadcastEventAsync(eventData);
        
        _logger.LogDebug("Broadcasted file processed: {Filename} - {Success}", filename, success);
    }

    public async Task BroadcastWatcherStatusAsync(bool running, bool enabled)
    {
        var eventData = new
        {
            type = "watcher_status",
            data = new
            {
                running = running,
                enabled = enabled
            }
        };

        await BroadcastEventAsync(eventData);
        
        _logger.LogDebug("Broadcasted watcher status: Running={Running}, Enabled={Enabled}", running, enabled);
    }

    public async Task BroadcastFileListUpdateAsync()
    {
        var eventData = new
        {
            type = "file_list_updated",
            data = new { }
        };

        await BroadcastEventAsync(eventData);
        
        _logger.LogDebug("Broadcasted file list update");
    }

    private async Task BroadcastEventAsync(object eventData)
    {
        var json = JsonSerializer.Serialize(eventData);
        var sseMessage = $"data: {json}\n\n";

        // Broadcast to all connected SSE clients
        var disconnectedClients = new List<string>();
        
        foreach (var (clientId, writer) in _sseClients)
        {
            try
            {
                await writer.WriteAsync(sseMessage);
                await writer.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send to SSE client {ClientId}, will disconnect", clientId);
                disconnectedClients.Add(clientId);
            }
        }

        // Clean up disconnected clients
        foreach (var clientId in disconnectedClients)
        {
            UnregisterSseClient(clientId);
        }
    }
}
