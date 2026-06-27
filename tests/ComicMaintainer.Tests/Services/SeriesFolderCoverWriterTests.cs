using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SeriesFolderCoverWriterTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly Mock<ISeriesLibraryService> _library = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _settings = new();
    private AppSettings _appSettings = new() { WriteCoverToSeriesFolder = true };

    public SeriesFolderCoverWriterTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"folder_cover_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _settings.Setup(s => s.CurrentValue).Returns(() => _appSettings);
    }

    private SeriesFolderCoverWriter CreateWriter()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _library.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return new SeriesFolderCoverWriter(
            scopeFactory,
            _settings.Object,
            new Mock<ILogger<SeriesFolderCoverWriter>>().Object);
    }

    [Fact]
    public async Task WriteAsync_ForceEnabled_WritesEvenWhenFeatureDisabled()
    {
        _appSettings = new AppSettings { WriteCoverToSeriesFolder = false };
        var seriesFolder = Path.Combine(_testDirectory, "Batman");
        Directory.CreateDirectory(seriesFolder);
        var coverSrc = Path.Combine(_testDirectory, "cover.jpg");
        var coverBytes = new byte[] { 1, 9, 9, 5 };
        await File.WriteAllBytesAsync(coverSrc, coverBytes);
        _library.Setup(l => l.GetFoldersForNormalizedKeyAsync("series-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesFolderDto> { new() { Directory = seriesFolder, FileCount = 1, TotalSize = coverBytes.Length } });

        await CreateWriter().WriteAsync("series-a", coverSrc, "image/jpeg", true);

        var destination = Path.Combine(seriesFolder, "cover.jpg");
        Assert.True(File.Exists(destination));
        Assert.Equal(coverBytes, await File.ReadAllBytesAsync(destination));
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDirectory, true); } catch { /* ignore */ }
    }
}
