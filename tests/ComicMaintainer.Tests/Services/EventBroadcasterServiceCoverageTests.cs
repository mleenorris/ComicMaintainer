using System.Text;
using System.Text.Json;
using ComicMaintainer.WebApi.Hubs;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class EventBroadcasterServiceCoverageTests
{
    private readonly Mock<IHubContext<ProgressHub>> _hubContext = new();
    private readonly Mock<IClientProxy> _clientProxy = new();
    private readonly EventBroadcasterService _service;

    public EventBroadcasterServiceCoverageTests()
    {
        _hubContext.Setup(h => h.Clients.Group(It.IsAny<string>())).Returns(_clientProxy.Object);
        _service = new EventBroadcasterService(Mock.Of<ILogger<EventBroadcasterService>>(), _hubContext.Object);
    }

    private static (MemoryStream Stream, StreamWriter Writer) CreateClient()
    {
        var stream = new MemoryStream();
        return (stream, new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true));
    }

    private static string ReadAll(MemoryStream stream) => Encoding.UTF8.GetString(stream.ToArray());

    [Fact]
    public async Task BroadcastFileListUpdateAsync_SendsFileListUpdatedEventToEverySseClient()
    {
        var (firstStream, firstWriter) = CreateClient();
        var (secondStream, secondWriter) = CreateClient();
        using (firstWriter)
        using (secondWriter)
        {
            _service.RegisterSseClient("first", firstWriter);
            _service.RegisterSseClient("second", secondWriter);

            await _service.BroadcastFileListUpdateAsync();
        }

        const string expected = "data: {\"type\":\"file_list_updated\",\"data\":{}}\n\n";
        Assert.Equal(expected, ReadAll(firstStream));
        Assert.Equal(expected, ReadAll(secondStream));
        // The file-list notification is SSE only; it is not pushed to any SignalR group.
        _hubContext.Verify(h => h.Clients, Times.Never);
    }

    [Fact]
    public async Task Broadcast_StalledSseClient_IsDisconnectedAfterTheSendTimeout()
    {
        var service = new EventBroadcasterService(
            Mock.Of<ILogger<EventBroadcasterService>>(), _hubContext.Object, TimeSpan.FromMilliseconds(100));
        var (healthyStream, healthyWriter) = CreateClient();
        using var stalledStream = new StalledStream();
        using var stalledWriter = new StreamWriter(stalledStream);
        using (healthyWriter)
        {
            service.RegisterSseClient("stalled", stalledWriter);
            service.RegisterSseClient("healthy", healthyWriter);

            // Completes although one client never accepts the event.
            await service.BroadcastWatcherStatusAsync(running: false, enabled: true).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, stalledStream.Writes);

            // The stalled client was dropped, so later broadcasts no longer wait for it.
            await service.BroadcastWatcherStatusAsync(running: true, enabled: true).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, stalledStream.Writes);
        }

        Assert.Equal(
            "data: {\"type\":\"watcher_status\",\"data\":{\"running\":false,\"enabled\":true}}\n\n"
            + "data: {\"type\":\"watcher_status\",\"data\":{\"running\":true,\"enabled\":true}}\n\n",
            ReadAll(healthyStream));
    }

    [Fact]
    public async Task Broadcast_StalledSseClient_AbortsItsConnection()
    {
        var service = new EventBroadcasterService(
            Mock.Of<ILogger<EventBroadcasterService>>(), _hubContext.Object, TimeSpan.FromMilliseconds(100));
        using var stalledStream = new StalledStream();
        using var stalledWriter = new StreamWriter(stalledStream);
        var aborts = 0;
        service.RegisterSseClient("stalled", stalledWriter, () => Interlocked.Increment(ref aborts));

        await service.BroadcastFileListUpdateAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, Volatile.Read(ref aborts));
        // The heartbeat path sees the client is gone, so the stream loop ends.
        Assert.False(await service.SendToSseClientAsync("stalled", ": heartbeat\n\n"));
    }

    [Fact]
    public async Task OverlappingSends_ToTheSameClient_AreSerializedAndKeepTheClientConnected()
    {
        using var stream = new GatedStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        var aborts = 0;
        _service.RegisterSseClient("client", writer, () => Interlocked.Increment(ref aborts));

        // The first send blocks in its flush; the others start while it is still in progress.
        var statusSend = _service.BroadcastWatcherStatusAsync(running: true, enabled: true);
        await stream.FirstWriteStarted.WaitAsync(TimeSpan.FromSeconds(10));
        var fileListSend = _service.BroadcastFileListUpdateAsync();
        var heartbeat = _service.SendToSseClientAsync("client", ": heartbeat\n\n");
        Assert.False(fileListSend.IsCompleted);
        Assert.False(heartbeat.IsCompleted);

        stream.Release();
        await Task.WhenAll(statusSend, fileListSend, heartbeat).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(await heartbeat);
        Assert.Equal(0, Volatile.Read(ref aborts));
        Assert.True(await _service.SendToSseClientAsync("client", ": heartbeat\n\n"));
        Assert.Equal(
            "data: {\"type\":\"watcher_status\",\"data\":{\"running\":true,\"enabled\":true}}\n\n"
            + "data: {\"type\":\"file_list_updated\",\"data\":{}}\n\n"
            + ": heartbeat\n\n"
            + ": heartbeat\n\n",
            Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>A client stream whose first write waits until <see cref="Release"/> is called.</summary>
    private sealed class GatedStream : MemoryStream
    {
        private readonly TaskCompletionSource _firstWriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstWriteStarted => _firstWriteStarted.Task;

        public void Release() => _gate.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _firstWriteStarted.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>A client stream whose writes never complete until they are cancelled.</summary>
    private sealed class StalledStream : MemoryStream
    {
        private int _writes;

        public int Writes => Volatile.Read(ref _writes);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writes);
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    [Fact]
    public async Task BroadcastFileListUpdateAsync_WithoutClients_DoesNotThrow()
    {
        await _service.BroadcastFileListUpdateAsync();

        _hubContext.Verify(h => h.Clients, Times.Never);
    }

    [Fact]
    public async Task BroadcastJobUpdateAsync_WithZeroTotal_ReportsZeroPercentage()
    {
        var jobId = Guid.NewGuid();
        var (stream, writer) = CreateClient();
        object?[]? signalRArgs = null;
        _clientProxy
            .Setup(c => c.SendCoreAsync("JobUpdate", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => signalRArgs = args)
            .Returns(Task.CompletedTask);

        using (writer)
        {
            _service.RegisterSseClient("client", writer);
            await _service.BroadcastJobUpdateAsync(jobId, "queued", processed: 0, total: 0, success: 0, errors: 0);
        }

        var payload = ReadAll(stream);
        Assert.StartsWith("data: ", payload);
        Assert.EndsWith("\n\n", payload);
        using var json = JsonDocument.Parse(payload["data: ".Length..^2]);
        Assert.Equal("job_updated", json.RootElement.GetProperty("type").GetString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(jobId.ToString(), data.GetProperty("job_id").GetString());
        Assert.Equal("queued", data.GetProperty("status").GetString());
        Assert.Equal(0, data.GetProperty("progress").GetProperty("total").GetInt32());
        Assert.Equal(0, data.GetProperty("progress").GetProperty("percentage").GetInt32());

        _hubContext.Verify(h => h.Clients.Group($"job-{jobId}"), Times.Once);
        var sent = Assert.Single(Assert.IsType<object?[]>(signalRArgs));
        using var sentJson = JsonDocument.Parse(JsonSerializer.Serialize(sent));
        Assert.Equal(0, sentJson.RootElement.GetProperty("progress").GetProperty("percentage").GetInt32());
    }
}
