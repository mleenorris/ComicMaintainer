using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class CompositeExternalSeriesMetadataServiceTests
{
    [Fact]
    public async Task LookupSeriesAsync_ReturnsFirstNonNullResult()
    {
        var firstProvider = new Mock<IExternalSeriesMetadataService>();
        firstProvider
            .Setup(provider => provider.LookupSeriesAsync("Batman", default))
            .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "Batman", Source = "FirstProvider" });

        var secondProvider = new Mock<IExternalSeriesMetadataService>();
        secondProvider
            .Setup(provider => provider.LookupSeriesAsync("Batman", default))
            .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "Batman", Source = "SecondProvider" });

        var service = new CompositeExternalSeriesMetadataService(
            [firstProvider.Object, secondProvider.Object],
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>());

        var result = await service.LookupSeriesAsync("Batman");

        Assert.NotNull(result);
        Assert.Equal("FirstProvider", result!.Source);
        secondProvider.Verify(provider => provider.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LookupSeriesAsync_FallsBackToNextProvider_WhenFirstReturnsNull()
    {
        var firstProvider = new Mock<IExternalSeriesMetadataService>();
        firstProvider
            .Setup(provider => provider.LookupSeriesAsync("Solo Leveling", default))
            .ReturnsAsync((ExternalSeriesMetadata?)null);

        var secondProvider = new Mock<IExternalSeriesMetadataService>();
        secondProvider
            .Setup(provider => provider.LookupSeriesAsync("Solo Leveling", default))
            .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "Solo Leveling", Source = "SecondProvider" });

        var service = new CompositeExternalSeriesMetadataService(
            [firstProvider.Object, secondProvider.Object],
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>());

        var result = await service.LookupSeriesAsync("Solo Leveling");

        Assert.NotNull(result);
        Assert.Equal("SecondProvider", result!.Source);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenAllProvidersReturnNull()
    {
        var firstProvider = new Mock<IExternalSeriesMetadataService>();
        firstProvider
            .Setup(provider => provider.LookupSeriesAsync(It.IsAny<string>(), default))
            .ReturnsAsync((ExternalSeriesMetadata?)null);

        var secondProvider = new Mock<IExternalSeriesMetadataService>();
        secondProvider
            .Setup(provider => provider.LookupSeriesAsync(It.IsAny<string>(), default))
            .ReturnsAsync((ExternalSeriesMetadata?)null);

        var service = new CompositeExternalSeriesMetadataService(
            [firstProvider.Object, secondProvider.Object],
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>());

        var result = await service.LookupSeriesAsync("Unknown Series");

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupSeriesAsync_ContinuesToNextProvider_WhenFirstThrows()
    {
        var firstProvider = new Mock<IExternalSeriesMetadataService>();
        firstProvider
            .Setup(provider => provider.LookupSeriesAsync(It.IsAny<string>(), default))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var secondProvider = new Mock<IExternalSeriesMetadataService>();
        secondProvider
            .Setup(provider => provider.LookupSeriesAsync("Berserk", default))
            .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "Berserk", Source = "SecondProvider" });

        var service = new CompositeExternalSeriesMetadataService(
            [firstProvider.Object, secondProvider.Object],
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>());

        var result = await service.LookupSeriesAsync("Berserk");

        Assert.NotNull(result);
        Assert.Equal("Berserk", result!.CanonicalTitle);
        Assert.Equal("SecondProvider", result.Source);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenNoProvidersRegistered()
    {
        var service = new CompositeExternalSeriesMetadataService(
            [],
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>());

        var result = await service.LookupSeriesAsync("Batman");

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupSeriesAsync_SkipsProviderWhileCoolingDown_AfterFailure()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);

        var failing = new Mock<IExternalSeriesMetadataService>();
        failing
            .Setup(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));

        var service = new CompositeExternalSeriesMetadataService(
            [failing.Object],
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>(),
            clock,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(1));

        // First call: provider is consulted and throws, returning null overall.
        Assert.Null(await service.LookupSeriesAsync("Batman"));
        failing.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        // Within the cooldown window, the provider must NOT be called again
        // (treated as failed so processing isn't held up).
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(await service.LookupSeriesAsync("Batman"));
        failing.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        // Past the cooldown the provider is consulted again.
        clock.Advance(TimeSpan.FromSeconds(25));
        Assert.Null(await service.LookupSeriesAsync("Batman"));
        failing.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task LookupSeriesAsync_DoublesBackoffOnRepeatedFailures()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);

        var failing = new Mock<IExternalSeriesMetadataService>();
        failing
            .Setup(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));

        var service = new CompositeExternalSeriesMetadataService(
            [failing.Object],
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>(),
            clock,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(1));

        // First failure -> 30s cooldown.
        await service.LookupSeriesAsync("Batman");
        clock.Advance(TimeSpan.FromSeconds(31));

        // Second failure -> 60s cooldown. After 31s the provider should still
        // be cooling down (not yet re-consulted).
        await service.LookupSeriesAsync("Batman");
        failing.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        clock.Advance(TimeSpan.FromSeconds(31));
        await service.LookupSeriesAsync("Batman");
        failing.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        clock.Advance(TimeSpan.FromSeconds(30));
        await service.LookupSeriesAsync("Batman");
        failing.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task LookupSeriesAsync_ResetsBackoff_AfterSuccessfulCall()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);

        var sequence = new Queue<Func<ExternalSeriesMetadata?>>();
        sequence.Enqueue(() => throw new HttpRequestException("boom"));
        sequence.Enqueue(() => new ExternalSeriesMetadata { CanonicalTitle = "Batman", Source = "P" });
        sequence.Enqueue(() => throw new HttpRequestException("boom2"));

        var provider = new Mock<IExternalSeriesMetadataService>();
        provider
            .Setup(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(sequence.Dequeue()()));

        var service = new CompositeExternalSeriesMetadataService(
            [provider.Object],
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>(),
            clock,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(1));

        // Failure -> cooldown.
        Assert.Null(await service.LookupSeriesAsync("Batman"));

        // Skipped while cooling down.
        Assert.Null(await service.LookupSeriesAsync("Batman"));
        provider.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        // After cooldown elapses, success clears the backoff.
        clock.Advance(TimeSpan.FromSeconds(31));
        var ok = await service.LookupSeriesAsync("Batman");
        Assert.NotNull(ok);

        // A subsequent failure is treated as the FIRST failure again (30s
        // cooldown), so the next call within 30s is skipped.
        Assert.Null(await service.LookupSeriesAsync("Batman"));
        provider.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(await service.LookupSeriesAsync("Batman"));
        provider.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task LookupSeriesAsync_TriesNextProvider_WhenFirstIsCoolingDown()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);

        var first = new Mock<IExternalSeriesMetadataService>();
        first
            .Setup(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("down"));

        var second = new Mock<IExternalSeriesMetadataService>();
        second
            .Setup(p => p.LookupSeriesAsync("Berserk", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "Berserk", Source = "Second" });

        var service = new CompositeExternalSeriesMetadataService(
            [first.Object, second.Object],
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>(),
            clock,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(1));

        // Trip the first provider into cooldown.
        var initial = await service.LookupSeriesAsync("Berserk");
        Assert.Equal("Second", initial!.Source);
        first.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        // Subsequent call: first is skipped (still cooling down), second still answers.
        var result = await service.LookupSeriesAsync("Berserk");
        Assert.Equal("Second", result!.Source);
        first.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        second.Verify(p => p.LookupSeriesAsync("Berserk", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
