using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// The startup hosted service runs the interrupted-write sweep once and never lets a failure of
/// the sweep stop the application.
/// </summary>
public sealed class ArchiveWriteRecoveryHostedServiceTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Join(Path.GetTempPath(), "archive-recovery-host-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestIO.TryDeleteDirectory(_root);

    [Fact]
    public async Task StartAsync_SweepsTheLibraryInTheBackground()
    {
        var library = Directory.CreateDirectory(Path.Join(_root, "library")).FullName;
        var leftover = ArchiveWriteRecoveryService.CreateTempPath(library);
        await File.WriteAllTextAsync(leftover, "half-written");
        File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddHours(-1));
        var recovery = new ArchiveWriteRecoveryService(
            new TestOptionsMonitor<AppSettings>(new AppSettings
            {
                WatchedDirectory = library,
                DuplicateDirectory = Path.Join(_root, "duplicates"),
                TempFileDirectory = Path.Join(_root, "temp")
            }),
            NullLogger<ArchiveWriteRecoveryService>.Instance);

        using var service = new ArchiveWriteRecoveryHostedService(recovery, NullLogger<ArchiveWriteRecoveryHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(Timeout);

        Assert.False(File.Exists(leftover));
    }

    [Fact]
    public async Task StartAsync_SweepFails_LogsTheErrorWithoutFaulting()
    {
        var settings = new Mock<IOptionsMonitor<AppSettings>>();
        settings.Setup(s => s.CurrentValue).Throws(new InvalidOperationException("settings unavailable"));
        var recovery = new ArchiveWriteRecoveryService(settings.Object, NullLogger<ArchiveWriteRecoveryService>.Instance);
        var logger = new Mock<ILogger<ArchiveWriteRecoveryHostedService>>();

        using var service = new ArchiveWriteRecoveryHostedService(recovery, logger.Object);
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(Timeout);

        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString() == "Failed to recover interrupted archive writes at startup"),
                It.Is<InvalidOperationException>(ex => ex.Message == "settings unavailable"),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
