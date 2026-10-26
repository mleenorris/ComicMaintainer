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
