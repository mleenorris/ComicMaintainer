using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Exercises the default interface members of
/// <see cref="IExternalSeriesMetadataService"/> through a provider that only
/// implements <see cref="IExternalSeriesMetadataService.LookupSeriesAsync"/>.
/// </summary>
public class IExternalSeriesMetadataServiceCoverageTests
{
    [Fact]
    public void ProviderName_DefaultsToTheImplementationTypeName()
    {
        IExternalSeriesMetadataService provider = new LookupOnlyProvider(_ => Task.FromResult<ExternalSeriesMetadata?>(null));

        Assert.Equal(nameof(LookupOnlyProvider), provider.ProviderName);
    }

    [Fact]
    public async Task SearchSeriesAsync_Default_WrapsTheSingleLookupResult()
    {
        var match = new ExternalSeriesMetadata { CanonicalTitle = "Berserk", Source = "Lookup" };
        var provider = new LookupOnlyProvider(_ => Task.FromResult<ExternalSeriesMetadata?>(match));

        var results = await ((IExternalSeriesMetadataService)provider).SearchSeriesAsync("Berserk", limit: 5);

        Assert.Same(match, Assert.Single(results));
        Assert.Equal(new[] { "Berserk" }, provider.Queries);
    }

    [Fact]
    public async Task SearchSeriesAsync_Default_ReturnsEmpty_WhenLookupFindsNothing()
    {
        IExternalSeriesMetadataService provider = new LookupOnlyProvider(_ => Task.FromResult<ExternalSeriesMetadata?>(null));

        var results = await provider.SearchSeriesAsync("Nothing");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchSeriesAsync_Default_DoesNotCompleteSuccessfully_WhenLookupFails()
    {
        IExternalSeriesMetadataService provider = new LookupOnlyProvider(
            _ => Task.FromException<ExternalSeriesMetadata?>(new HttpRequestException("boom")));

        var search = provider.SearchSeriesAsync("Berserk");

        await Assert.ThrowsAnyAsync<Exception>(() => search);
        Assert.False(search.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SearchSeriesAsync_Default_FailedLookup_IsTreatedAsProviderFailureByComposite()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var provider = new LookupOnlyProvider(
            _ => Task.FromException<ExternalSeriesMetadata?>(new HttpRequestException("boom")));
        var composite = new CompositeExternalSeriesMetadataService(
            new IExternalSeriesMetadataService[] { provider },
            NullLogger<CompositeExternalSeriesMetadataService>.Instance,
            clock,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(1));

        Assert.Empty(await composite.SearchSeriesAsync("Berserk"));
        Assert.Empty(await composite.SearchSeriesAsync("Berserk"));

        // The second search is skipped because the provider is cooling down.
        Assert.Single(provider.Queries);
    }

    [Fact]
    public async Task CheckHealthAsync_Default_ReportsThatNoHealthCheckIsImplemented()
    {
        IExternalSeriesMetadataService provider = new LookupOnlyProvider(_ => Task.FromResult<ExternalSeriesMetadata?>(null));

        var health = await provider.CheckHealthAsync();

        Assert.Equal(nameof(LookupOnlyProvider), health.Name);
        Assert.True(health.Enabled);
        Assert.True(health.Configured);
        Assert.Null(health.Reachable);
        Assert.Equal("Health check not implemented for this provider", health.StatusMessage);
    }

    private sealed class LookupOnlyProvider : IExternalSeriesMetadataService
    {
        private readonly Func<string, Task<ExternalSeriesMetadata?>> _lookup;

        public LookupOnlyProvider(Func<string, Task<ExternalSeriesMetadata?>> lookup) => _lookup = lookup;

        public List<string> Queries { get; } = new();

        public Task<ExternalSeriesMetadata?> LookupSeriesAsync(string seriesName, CancellationToken cancellationToken = default)
        {
            Queries.Add(seriesName);
            return _lookup(seriesName);
        }
    }
}
