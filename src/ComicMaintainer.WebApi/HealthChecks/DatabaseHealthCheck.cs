using ComicMaintainer.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ComicMaintainer.WebApi.HealthChecks;

/// <summary>
/// Readiness check that verifies the SQLite database is reachable.
/// </summary>
/// <remarks>
/// Implemented directly against <see cref="IDbContextFactory{TContext}"/> rather
/// than pulling in the EF Core health-check package, since a single connectivity
/// probe is all the readiness endpoint needs.
/// </remarks>
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    public DatabaseHealthCheck(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        ILogger<DatabaseHealthCheck> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            if (await db.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Healthy("Database is reachable.");
            }

            return HealthCheckResult.Unhealthy("Database is not reachable.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database readiness check failed.");
            return HealthCheckResult.Unhealthy("Database readiness check threw an exception.", ex);
        }
    }
}
