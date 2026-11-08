using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class CompositeExternalSeriesMetadataServiceTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    private CompositeExternalSeriesMetadataService CreateService(params IExternalSeriesMetadataService[] providers)
        => new(
            providers,
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>(),
            _clock,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(1));

    private static Mock<IExternalSeriesMetadataService> Provider(string name)
    {
        var mock = new Mock<IExternalSeriesMetadataService>();
        mock.SetupGet(p => p.ProviderName).Returns(name);
        return mock;
    }

    private static Mock<IExternalSeriesMetadataService> LookupProvider(string name, ExternalSeriesMetadata? result)
    {
        var mock = Provider(name);
        mock.Setup(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return mock;
    }

    private static Mock<IExternalSeriesMetadataService> FailingProvider(string name, Exception error)
    {
        var mock = Provider(name);
        mock.Setup(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(error);
        return mock;
    }

    private static ExternalSeriesMetadata Meta(string title, string source, params string[] aliases)
        => new() { CanonicalTitle = title, Source = source, Aliases = aliases.ToList() };

    private static void VerifyLookups(Mock<IExternalSeriesMetadataService> provider, int times)
        => provider.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(times));

    [Fact]
    public async Task LookupSeriesAsync_AsksEveryProvider_AndKeepsTheBestScoringMatch()
    {
        // The first provider only matches by alias; a later one has the exact
        // canonical title, which wins although it was not first.
        var aliasMatch = LookupProvider("MangaDex", Meta("Shingeki no Kyojin", "MangaDex", "Attack on Titan"));
        var exactMatch = LookupProvider("AniListManga", Meta("Attack on Titan", "AniListManga"));
        var service = CreateService(aliasMatch.Object, exactMatch.Object);

        var result = await service.LookupSeriesAsync("Attack on Titan");

        Assert.Equal("AniListManga", result!.Source);
        VerifyLookups(aliasMatch, 1);
        VerifyLookups(exactMatch, 1);
    }

    [Fact]
    public async Task LookupSeriesAsync_TieKeepsTheEarlierProvider()
    {
        var first = LookupProvider("ComicVine", Meta("Batman", "ComicVine"));
        var second = LookupProvider("MangaDex", Meta("Batman", "MangaDex"));
        var service = CreateService(first.Object, second.Object);

        var result = await service.LookupSeriesAsync("Batman");

        Assert.Equal("ComicVine", result!.Source);
    }

    [Fact]
    public async Task LookupSeriesAsync_NeverAcceptsAFuzzyCandidate()
    {
        // A provider that hands back its top fuzzy hit does not get it accepted.
        var fuzzy = LookupProvider("Custom", Meta("Batman Beyond", "Custom"));
        var service = CreateService(fuzzy.Object);

        Assert.Null(await service.LookupSeriesAsync("Batman"));
    }

    [Fact]
    public async Task LookupSeriesAsync_AsksProvidersConcurrently()
    {
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Provider("First");
        first.Setup(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken token) =>
            {
                // Only completes once the second provider has been asked, which
                // a sequential chain would never do: it would wait out the
                // composite's per-provider budget and fail the lookup instead.
                // No wall-clock limit of its own, so a slow machine cannot fail it.
                await secondStarted.Task.WaitAsync(token);
                return Meta("Berserk", "First");
            });
        var second = Provider("Second");
        second.Setup(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                secondStarted.TrySetResult();
                return Task.FromResult<ExternalSeriesMetadata?>(null);
            });
        var service = CreateService(first.Object, second.Object);

        var result = await service.LookupSeriesAsync("Berserk");

        Assert.Equal("First", result!.Source);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenEveryProviderAnswersNoMatch()
    {
        var service = CreateService(LookupProvider("A", null).Object, LookupProvider("B", null).Object);

        Assert.Null(await service.LookupSeriesAsync("Unknown Series"));
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenNoProvidersRegistered()
    {
        Assert.Null(await CreateService().LookupSeriesAsync("Batman"));
    }

    [Fact]
    public async Task LookupSeriesAsync_Throws_WhenNoMatchAndAProviderFailed()
    {
        // The failing provider might have had the series: this is not "not found".
        var failing = FailingProvider("MangaDex", new ExternalMetadataUnavailableException(
            "MangaDex", ExternalMetadataFailureKind.Timeout, "MangaDex did not answer within 15s"));
        var empty = LookupProvider("AniListManga", null);
        var service = CreateService(failing.Object, empty.Object);

        var failure = await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(
            () => service.LookupSeriesAsync("Solo Leveling"));

        Assert.Equal(new[] { "MangaDex" }, failure.Providers);
        Assert.Equal(ExternalMetadataFailureKind.Timeout, failure.Kind);
        Assert.Contains("MangaDex did not answer within 15s", failure.Message);
        VerifyLookups(empty, 1);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsTheMatch_WhenAnotherProviderFailed()
    {
        var failing = FailingProvider("First", new HttpRequestException("Connection refused"));
        var answering = LookupProvider("Second", Meta("Berserk", "Second"));
        var service = CreateService(failing.Object, answering.Object);

        var result = await service.LookupSeriesAsync("Berserk");

        Assert.Equal("Second", result!.Source);
    }

    [Fact]
    public async Task LookupSeriesAsync_ProviderThatExceedsItsBudget_FailsAsATimeoutAndCoolsDown()
    {
        var stalled = Provider("Stalled");
        stalled.Setup(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            });
        var service = new CompositeExternalSeriesMetadataService(
            [stalled.Object],
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>(),
            _clock,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(1),
            providerCallTimeout: TimeSpan.FromMilliseconds(100));

        var failure = await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(
            () => service.LookupSeriesAsync("Batman"));

        Assert.Equal(ExternalMetadataFailureKind.Timeout, failure.Kind);
        Assert.Equal(new[] { "Stalled" }, failure.Providers);

        // While cooling down the provider is not waited on again.
        var skipped = await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(
            () => service.LookupSeriesAsync("Batman"));
        Assert.Contains("skipped after recent failures", skipped.Message);
        VerifyLookups(stalled, 1);
    }

    [Fact]
    public async Task LookupSeriesAsync_SkipsProviderWhileCoolingDown_AfterFailure()
    {
        var failing = FailingProvider("Failing", new HttpRequestException("boom"));
        var service = CreateService(failing.Object);

        // First call: the provider is consulted and fails.
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        VerifyLookups(failing, 1);

        // Within the cooldown window, the provider is NOT called again; the
        // lookup still reports it as unavailable rather than "not found".
        _clock.Advance(TimeSpan.FromSeconds(10));
        var skipped = await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        Assert.Equal(new[] { "Failing" }, skipped.Providers);
        Assert.Equal(TimeSpan.FromSeconds(20), skipped.RetryAfter);
        VerifyLookups(failing, 1);

        // Past the cooldown the provider is consulted again.
        _clock.Advance(TimeSpan.FromSeconds(25));
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        VerifyLookups(failing, 2);
    }

    [Fact]
    public async Task LookupSeriesAsync_DoublesBackoffOnRepeatedFailures()
    {
        var failing = FailingProvider("Failing", new HttpRequestException("boom"));
        var service = CreateService(failing.Object);

        // First failure -> 30s cooldown.
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        _clock.Advance(TimeSpan.FromSeconds(31));

        // Second failure -> 60s cooldown. After 31s the provider should still
        // be cooling down (not yet re-consulted).
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        VerifyLookups(failing, 2);

        _clock.Advance(TimeSpan.FromSeconds(31));
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        VerifyLookups(failing, 2);

        _clock.Advance(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        VerifyLookups(failing, 3);
    }

    [Fact]
    public async Task LookupSeriesAsync_HonorsALongerRetryAfterThanTheBackoff()
    {
        var throttled = FailingProvider("ComicVine", new ExternalMetadataUnavailableException(
            "ComicVine", ExternalMetadataFailureKind.RateLimited, "budget used up", TimeSpan.FromMinutes(20)));
        var service = CreateService(throttled.Object);

        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        _clock.Advance(TimeSpan.FromMinutes(19));
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        VerifyLookups(throttled, 1);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        VerifyLookups(throttled, 2);
    }

    [Fact]
    public async Task LookupSeriesAsync_OurOwnPacing_IsNotHeldAgainstTheProvider()
    {
        var paced = FailingProvider("AniListManga", new ExternalMetadataUnavailableException(
            "AniListManga", ExternalMetadataFailureKind.Paced, "AniListManga is at its request pace limit"));
        var service = CreateService(paced.Object);

        // The series may exist there, so this is not "not found"...
        var failure = await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(
            () => service.LookupSeriesAsync("Batman"));
        Assert.Equal(ExternalMetadataFailureKind.Paced, failure.Kind);

        // ...but the provider is asked again at once: no cooldown was started.
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        VerifyLookups(paced, 2);
    }

    [Fact]
    public async Task LookupSeriesAsync_OurOwnPacing_DoesNotClearAnExistingCooldown()
    {
        var calls = 0;
        var provider = Provider("AniListManga");
        provider.Setup(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 2
                ? Task.FromException<ExternalSeriesMetadata?>(new ExternalMetadataUnavailableException(
                    "AniListManga", ExternalMetadataFailureKind.Paced, "at its request pace limit"))
                : Task.FromException<ExternalSeriesMetadata?>(new HttpRequestException("boom")));
        var service = CreateService(provider.Object);

        // First failure: 30 s cooldown. Past it, a paced call says nothing
        // about recovery, so it must leave the failure count alone.
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        _clock.Advance(TimeSpan.FromSeconds(31));
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        VerifyLookups(provider, 2);

        // The next real failure is the second in a row: the wait doubles to 60 s.
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        VerifyLookups(provider, 3);
        _clock.Advance(TimeSpan.FromSeconds(31));
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        VerifyLookups(provider, 3);
    }

    [Fact]
    public async Task SearchSeriesAsync_ProviderThatExceedsTheSearchBudget_FailsWithinIt()
    {
        var stalled = Provider("Stalled");
        stalled.Setup(p => p.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, int _, CancellationToken token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Array.Empty<ExternalSeriesMetadata>();
            });
        var service = new CompositeExternalSeriesMetadataService(
            [stalled.Object],
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>(),
            _clock,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(1),
            searchCallTimeout: TimeSpan.FromMilliseconds(100));

        var failure = await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(
            () => service.SearchSeriesAsync("Batman"));

        Assert.Equal(ExternalMetadataFailureKind.Timeout, failure.Kind);
    }

    [Fact]
    public void ForgetNoMatch_ReachesEveryProvider()
    {
        var first = Provider("First");
        var second = Provider("Second");

        CreateService(first.Object, second.Object).ForgetNoMatch("Batman");

        first.Verify(p => p.ForgetNoMatch("Batman"), Times.Once);
        second.Verify(p => p.ForgetNoMatch("Batman"), Times.Once);
    }

    [Fact]
    public async Task LookupSeriesAsync_ResetsBackoff_AfterAnAnswer()
    {
        var sequence = new Queue<Func<ExternalSeriesMetadata?>>();
        sequence.Enqueue(() => throw new HttpRequestException("boom"));
        sequence.Enqueue(() => null);
        sequence.Enqueue(() => throw new HttpRequestException("boom2"));

        var provider = Provider("P");
        provider
            .Setup(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(sequence.Dequeue()()));
        var service = CreateService(provider.Object);

        // Failure -> cooldown; skipped while cooling down.
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        VerifyLookups(provider, 1);

        // After cooldown elapses, an answer (even "no match") clears the backoff.
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(await service.LookupSeriesAsync("Batman"));

        // A subsequent failure is treated as the FIRST failure again (30s
        // cooldown), so the next call within 30s is skipped.
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        VerifyLookups(provider, 3);

        _clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupSeriesAsync("Batman"));
        VerifyLookups(provider, 3);
    }

    [Fact]
    public async Task LookupSeriesAsync_TriesOtherProviders_WhenOneIsCoolingDown()
    {
        var first = FailingProvider("First", new HttpRequestException("down"));
        var second = LookupProvider("Second", Meta("Berserk", "Second"));
        var service = CreateService(first.Object, second.Object);

        // Trip the first provider into cooldown.
        Assert.Equal("Second", (await service.LookupSeriesAsync("Berserk"))!.Source);
        VerifyLookups(first, 1);

        // Subsequent call: first is skipped (still cooling down), second still answers.
        Assert.Equal("Second", (await service.LookupSeriesAsync("Berserk"))!.Source);
        VerifyLookups(first, 1);
        VerifyLookups(second, 2);
    }

    [Fact]
    public async Task SearchSeriesAsync_InterleavesProviders_SoEveryProviderIsRepresented()
    {
        // MangaDex fills the whole limit on its own; AniList still gets its
        // share of the manual-match picker.
        var mangaDex = Provider("MangaDex");
        mangaDex.Setup(p => p.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, 4).Select(i => Meta($"M{i}", "MangaDex")).ToList());
        var aniList = Provider("AniListManga");
        aniList.Setup(p => p.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Meta("A1", "AniListManga"), Meta("A2", "AniListManga") });
        var service = CreateService(mangaDex.Object, aniList.Object);

        var results = await service.SearchSeriesAsync("query", 4);

        Assert.Equal(new[] { "M1", "A1", "M2", "A2" }, results.Select(r => r.CanonicalTitle));
        mangaDex.Verify(p => p.SearchSeriesAsync("query", 4, It.IsAny<CancellationToken>()), Times.Once);
        aniList.Verify(p => p.SearchSeriesAsync("query", 4, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchSeriesAsync_ReturnsWhatAnswered_WhenAProviderFails()
    {
        var failing = Provider("MangaDex");
        failing.Setup(p => p.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));
        var answering = Provider("AniListManga");
        answering.Setup(p => p.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Meta("A1", "AniListManga") });
        var service = CreateService(failing.Object, answering.Object);

        var results = await service.SearchSeriesAsync("query");

        Assert.Equal("A1", Assert.Single(results).CanonicalTitle);
    }

    [Fact]
    public async Task SearchSeriesAsync_Throws_WhenNothingWasFoundAndAProviderFailed()
    {
        var failing = Provider("MangaDex");
        failing.Setup(p => p.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));
        var empty = Provider("AniListManga");
        empty.Setup(p => p.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ExternalSeriesMetadata>());
        var service = CreateService(failing.Object, empty.Object);

        var failure = await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(
            () => service.SearchSeriesAsync("query"));

        Assert.Equal(new[] { "MangaDex" }, failure.Providers);
        Assert.Contains("MangaDex failed: boom", failure.Message);
    }

    [Fact]
    public async Task LookupMatchAsync_AsksOnlyTheProviderThatSuppliedTheMatch()
    {
        var comicVine = Provider("ComicVine");
        var mangaDex = Provider("MangaDex");
        mangaDex.Setup(p => p.LookupMatchAsync("mangadex", "abc", "Solo Leveling", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Meta("Solo Leveling", "MangaDex"));
        var aniList = Provider("AniListManga");
        var service = CreateService(comicVine.Object, mangaDex.Object, aniList.Object);

        var result = await service.LookupMatchAsync("mangadex", "abc", "Solo Leveling");

        Assert.Equal("MangaDex", result!.Source);
        foreach (var other in new[] { comicVine, aniList })
        {
            other.Verify(p => p.LookupMatchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            other.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task LookupMatchAsync_UnknownSource_ReturnsNullWithoutAskingAnyone()
    {
        var mangaDex = Provider("MangaDex");
        var service = CreateService(mangaDex.Object);

        Assert.Null(await service.LookupMatchAsync("RetiredProvider", "1", "Batman"));
        mangaDex.Verify(p => p.LookupMatchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        mangaDex.Verify(p => p.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LookupMatchAsync_ProviderFailure_IsThrownAndBacksTheProviderOff()
    {
        var mangaDex = Provider("MangaDex");
        mangaDex.Setup(p => p.LookupMatchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));
        var service = CreateService(mangaDex.Object);

        await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupMatchAsync("MangaDex", "abc", "X"));
        var skipped = await Assert.ThrowsAsync<ExternalMetadataUnavailableException>(() => service.LookupMatchAsync("MangaDex", "abc", "X"));

        Assert.Contains("skipped after recent failures", skipped.Message);
        mangaDex.Verify(p => p.LookupMatchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
