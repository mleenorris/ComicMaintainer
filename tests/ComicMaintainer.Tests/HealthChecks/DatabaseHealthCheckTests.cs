using ComicMaintainer.Core.Data;
using ComicMaintainer.WebApi.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.HealthChecks;

public class DatabaseHealthCheckTests : IDisposable
{
    private readonly Mock<ILogger<DatabaseHealthCheck>> _logger = new();
    private readonly string _missingDirectory = Path.Combine(Path.GetTempPath(), "dbhealth_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_missingDirectory))
        {
            Directory.Delete(_missingDirectory, recursive: true);
        }
    }

    private async Task<HealthCheckResult> CheckAsync(IDbContextFactory<ComicMaintainerDbContext> factory)
    {
        var check = new DatabaseHealthCheck(factory, _logger.Object);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("database", check, HealthStatus.Unhealthy, tags: null)
        };
        return await check.CheckHealthAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task ReachableDatabase_IsHealthy()
    {
        var options = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
            .UseInMemoryDatabase("dbhealth_" + Guid.NewGuid().ToString("N"))
            .Options;

        var result = await CheckAsync(new Factory(() => new ComicMaintainerDbContext(options)));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("Database is reachable.", result.Description);
        Assert.Null(result.Exception);
    }

    [Fact]
    public async Task UnreachableDatabase_IsUnhealthyWithoutException()
    {
        var options = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_missingDirectory, "missing.db")}")
            .Options;

        var result = await CheckAsync(new Factory(() => new ComicMaintainerDbContext(options)));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Database is not reachable.", result.Description);
        Assert.Null(result.Exception);
        Assert.False(File.Exists(Path.Combine(_missingDirectory, "missing.db")));
    }

    [Fact]
    public async Task FactoryThrows_IsUnhealthyAndLogsWarning()
    {
        var failure = new InvalidOperationException("database locked");

        var result = await CheckAsync(new Factory(() => throw failure));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Database readiness check threw an exception.", result.Description);
        Assert.Same(failure, result.Exception);
        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString() == "Database readiness check failed."),
                failure,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    private sealed class Factory(Func<ComicMaintainerDbContext> create) : IDbContextFactory<ComicMaintainerDbContext>
    {
        public ComicMaintainerDbContext CreateDbContext() => create();
    }
}
