using System.Collections.Concurrent;
using System.Text.Json;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
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
/// SSE writer, or one that does not accept an event within
/// <see cref="DefaultSseSendTimeout"/>, is silently disconnected: it is removed from the
/// registry and its connection is aborted, so a stalled client cannot hold up a broadcast
/// indefinitely and its browser reconnects instead of silently missing updates.
/// </para>
/// <para>
/// Every write to a client, including the heartbeats sent through
/// <see cref="SendToSseClientAsync"/>, is serialized per client so overlapping
/// broadcasts never write to the same <see cref="StreamWriter"/> at once.
/// </para>
/// </summary>
public class EventBroadcasterService : IEventBroadcaster
{
    /// <summary>Longest an SSE client may take to accept an event before it is disconnected.</summary>
    public static readonly TimeSpan DefaultSseSendTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<EventBroadcasterService> _logger;
    private readonly IHubContext<ProgressHub> _hubContext;
    private readonly ConcurrentDictionary<string, SseClient> _sseClients = new();
    private readonly TimeSpan _sseSendTimeout;

    public EventBroadcasterService(
        ILogger<EventBroadcasterService> logger,
        IHubContext<ProgressHub> hubContext,
        TimeSpan? sseSendTimeout = null)
    {
        _logger = logger;
        _hubContext = hubContext;
        _sseSendTimeout = sseSendTimeout ?? DefaultSseSendTimeout;
    }

    /// <summary>
    /// Registers an SSE client. <paramref name="abort"/> is invoked when a send to the
    /// client fails or times out, so the owner can end the response.
    /// </summary>
    public void RegisterSseClient(string clientId, StreamWriter writer, Action? abort = null)
    {
        _sseClients[clientId] = new SseClient(writer, abort);
        _logger.LogDebug("SSE client registered: {ClientId}", clientId);
    }

    /// <summary>
    /// Sends a raw SSE message (such as a heartbeat) to one client, serialized with
    /// broadcasts to that client. Returns <c>false</c> if the client is not registered or
    /// the send failed, in which case the client has been disconnected.
    /// </summary>
    public Task<bool> SendToSseClientAsync(string clientId, string message)
    {
        return _sseClients.TryGetValue(clientId, out var client)
            ? SendAsync(clientId, client, message)
            : Task.FromResult(false);
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
        
        _logger.LogDebug("Broadcasted file processed: {Filename} - {Success}", LoggingHelper.SanitizeForLog(filename), success);
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
        foreach (var (clientId, client) in _sseClients)
        {
            await SendAsync(clientId, client, sseMessage);
        }
    }

    private async Task<bool> SendAsync(string clientId, SseClient client, string message)
    {
        using var timeout = new CancellationTokenSource(_sseSendTimeout);
        try
        {
            await client.Gate.WaitAsync(timeout.Token);
            try
            {
                await client.Writer.WriteAsync(message.AsMemory(), timeout.Token);
                await client.Writer.FlushAsync(timeout.Token);
            }
            finally
            {
                client.Gate.Release();
            }
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Failed to send to SSE client {ClientId}, will disconnect", clientId);
            Disconnect(clientId, client);
            return false;
        }
    }

    private void Disconnect(string clientId, SseClient client)
    {
        // Only remove this exact registration, never a newer one under the same id.
        if (_sseClients.TryRemove(KeyValuePair.Create(clientId, client)))
        {
            _logger.LogDebug("SSE client unregistered: {ClientId}", clientId);
        }

        try
        {
            client.Abort?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Failed to abort SSE client {ClientId}", clientId);
        }
    }

    private sealed class SseClient(StreamWriter writer, Action? abort)
    {
        public StreamWriter Writer { get; } = writer;
        public Action? Abort { get; } = abort;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
