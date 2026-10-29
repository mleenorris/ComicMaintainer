using System.Text.RegularExpressions;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SeriesNameResolverCoverageTests
{
    private readonly AppSettings _settings = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _options = new();
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly Mock<IExternalSeriesMetadataService> _external = new();
    private readonly ListLogger _logger = new();

    public SeriesNameResolverCoverageTests()
    {
        _options.Setup(o => o.CurrentValue).Returns(_settings);
        _cache.Setup(c => c.NormalizeKey(It.IsAny<string?>())).Returns<string?>(NormalizeLikeCache);
    }

    private SeriesNameResolver CreateResolver(bool withCache = true, bool withExternal = true) =>
        new(
            _options.Object,
            _logger,
            withCache ? _cache.Object : null,
            withExternal ? _external.Object : null);

    // Mirrors SeriesMetadataCacheService.NormalizeKey so the mocked cache is keyed the same way.
    private static string NormalizeLikeCache(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown-series";
        }
        var normalized = Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown-series" : normalized;
    }

    /// <summary>
    /// Configures successive <c>GetAsync(key)</c> calls; the last response repeats.
    /// A response that throws models a transient cache failure.
    /// </summary>
    private void SetupGetAsyncSequence(string key, params Func<SeriesMetadataCacheRecord?>[] responses)
    {
        var call = 0;
        _cache.Setup(c => c.GetAsync(key, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var index = Math.Min(call++, responses.Length - 1);
                return Task.FromResult(responses[index]());
            });
    }

    private void VerifyNoAliasWrites() =>
        _cache.Verify(c => c.SetUserAliasesAsync(
            It.IsAny<string>(),
            It.IsAny<IEnumerable<string>>(),
            It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task ResolveAsync_UserSelectedRecord_MutateCache_AppendsFolderToExistingUserAliases()
    {
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one-piece",
            CanonicalTitle = "One Piece",
            SeriesName = "One Piece (Pinned)",
            UserAliases = { "Existing Alias" }
        };
        _cache.Setup(c => c.GetAsync("one-piece", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            "/library/OP/c1.cbz",
            new ComicMetadata { Series = "One Piece" },
            mutateCache: true);

        Assert.Equal(SeriesNameResolutionStep.UserSelectedName, result.WinningStep);
        Assert.Equal("One Piece (Pinned)", result.ResolvedSeries);
        Assert.Equal("one-piece", result.MatchedCacheKey);
        Assert.Equal(new[] { "OP", "One Piece" }, result.Candidates);
        _cache.Verify(c => c.SetUserAliasesAsync(
            "One Piece",
            It.Is<IEnumerable<string>>(a => a.SequenceEqual(new[] { "Existing Alias", "OP" })),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("Added folder name 'OP' as user alias for series 'One Piece'"));
    }

    [Theory]
    [InlineData(null, "(none)")]
    [InlineData("ja", "'ja'")]
    public async Task ResolveAsync_MatchedRecordWithoutSeriesLanguage_UsesGlobalLanguageAndCreatesFolderAlias(
        string? globalLanguage, string expectedLanguageText)
    {
        _settings.DefaultPreferredLanguage = globalLanguage;
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "op",
            CanonicalTitle = "One Piece",
            LookupStatus = "SUCCESS",
            PreferredLanguage = "  "
        };
        // Exact-key lookup misses, the title scan finds the record.
        _cache.Setup(c => c.GetByTitleAsync("OP", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync("/library/OP/c1.cbz", metadata: null, mutateCache: true);

        Assert.Equal(SeriesNameResolutionStep.MatchedCache, result.WinningStep);
        Assert.Equal("One Piece", result.ResolvedSeries);
        Assert.Equal(globalLanguage, result.AppliedLanguage);
        Assert.Contains(expectedLanguageText, result.Explanation);
        // No record is keyed by the resolved title yet, so one is created with the folder as sole alias.
        _cache.Verify(c => c.SetUserAliasesAsync(
            "One Piece",
            It.Is<IEnumerable<string>>(a => a.SequenceEqual(new[] { "OP" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("alias")]
    [InlineData("userAlias")]
    public async Task ResolveAsync_FolderAlreadyKnownOnResolvedRecord_DoesNotWriteAlias(string knownAs)
    {
        _cache.Setup(c => c.GetAsync("op", It.IsAny<CancellationToken>())).ReturnsAsync(new SeriesMetadataCacheRecord
        {
            NormalizedKey = "op",
            CanonicalTitle = "One Piece",
            LookupStatus = "manual_match"
        });
        var resolvedRecord = knownAs switch
        {
            "canonical" => new SeriesMetadataCacheRecord { NormalizedKey = "one-piece", CanonicalTitle = "op" },
            "alias" => new SeriesMetadataCacheRecord { NormalizedKey = "one-piece", CanonicalTitle = "One Piece", Aliases = { "Op" } },
            _ => new SeriesMetadataCacheRecord { NormalizedKey = "one-piece", CanonicalTitle = "One Piece", UserAliases = { "oP" } }
        };
        _cache.Setup(c => c.GetAsync("one-piece", It.IsAny<CancellationToken>())).ReturnsAsync(resolvedRecord);
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync("/library/OP/c1.cbz", metadata: null, mutateCache: true);

        Assert.Equal(SeriesNameResolutionStep.MatchedCache, result.WinningStep);
        Assert.Equal("One Piece", result.ResolvedSeries);
        VerifyNoAliasWrites();
    }

    [Fact]
    public async Task ResolveAsync_ResolvedRecordWithNullAliasLists_WritesFolderAsOnlyAlias()
    {
        _cache.Setup(c => c.GetAsync("op", It.IsAny<CancellationToken>())).ReturnsAsync(new SeriesMetadataCacheRecord
        {
            NormalizedKey = "op",
            CanonicalTitle = "One Piece",
            LookupStatus = "manual"
        });
        _cache.Setup(c => c.GetAsync("one-piece", It.IsAny<CancellationToken>())).ReturnsAsync(new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one-piece",
            CanonicalTitle = "One Piece",
            Aliases = null!,
            UserAliases = null!
        });
        var resolver = CreateResolver();

        await resolver.ResolveAsync("/library/OP/c1.cbz", metadata: null, mutateCache: true);

        _cache.Verify(c => c.SetUserAliasesAsync(
            "One Piece",
            It.Is<IEnumerable<string>>(a => a.SequenceEqual(new[] { "OP" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_ResolvedTitleNormalizesToBlankKey_SkipsAliasWrite()
    {
        _cache.Setup(c => c.GetAsync("op", It.IsAny<CancellationToken>())).ReturnsAsync(new SeriesMetadataCacheRecord
        {
            NormalizedKey = "op",
            CanonicalTitle = "☆☆☆",
            LookupStatus = "success"
        });
        _cache.Setup(c => c.NormalizeKey("☆☆☆")).Returns(string.Empty);
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync("/library/OP/c1.cbz", metadata: null, mutateCache: true);

        Assert.Equal("☆☆☆", result.ResolvedSeries);
        _cache.Verify(c => c.GetAsync(string.Empty, It.IsAny<CancellationToken>()), Times.Never);
        VerifyNoAliasWrites();
    }

    [Fact]
    public async Task ResolveAsync_AliasWriteFails_LogsWarningAndStillResolves()
    {
        _cache.Setup(c => c.GetAsync("op", It.IsAny<CancellationToken>())).ReturnsAsync(new SeriesMetadataCacheRecord
        {
            NormalizedKey = "op",
            CanonicalTitle = "One Piece",
            LookupStatus = "success"
        });
        _cache.Setup(c => c.SetUserAliasesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("database is locked"));
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync("/library/OP/c1.cbz", metadata: null, mutateCache: true);

        Assert.Equal(SeriesNameResolutionStep.MatchedCache, result.WinningStep);
        Assert.Equal("One Piece", result.ResolvedSeries);
        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Exception is InvalidOperationException
            && e.Message.Contains("Failed to add folder name OP as alias for series One Piece"));
    }

    [Fact]
    public async Task ResolveAsync_BlankNormalizedKey_FallsBackToTitleScanWithoutKeyLookup()
    {
        _cache.Setup(c => c.NormalizeKey("★★★")).Returns(string.Empty);
        _cache.Setup(c => c.GetByTitleAsync("★★★", It.IsAny<CancellationToken>())).ReturnsAsync(new SeriesMetadataCacheRecord
        {
            NormalizedKey = "stars",
            CanonicalTitle = "Stars",
            LookupStatus = "manual"
        });
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync("/library/★★★/c1.cbz", metadata: null, mutateCache: false);

        Assert.Equal(SeriesNameResolutionStep.MatchedCache, result.WinningStep);
        Assert.Equal("Stars", result.ResolvedSeries);
        Assert.Equal("stars", result.MatchedCacheKey);
        _cache.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_UserSelectedLookupFails_MatchedStepStillHonoursPinnedName()
    {
        // The first (user-selected) cache read fails transiently; the second
        // (matched) read returns a record carrying a pinned name, which must
        // still be reported as the user's selection with no applied language.
        SetupGetAsyncSequence(
            "one-piece",
            () => throw new TimeoutException("cache busy"),
            () => new SeriesMetadataCacheRecord
            {
                NormalizedKey = "one-piece",
                CanonicalTitle = "One Piece",
                SeriesName = "Wan Piisu",
                PreferredLanguage = "ja"
            });
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            "/library/One Piece/c1.cbz",
            new ComicMetadata { Series = "One Piece" },
            mutateCache: false);

        Assert.Equal(SeriesNameResolutionStep.UserSelectedName, result.WinningStep);
        Assert.Equal("Wan Piisu", result.ResolvedSeries);
        Assert.Null(result.AppliedLanguage);
        Assert.Contains("has user-selected series name 'Wan Piisu'", result.Explanation);
        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Exception is TimeoutException
            && e.Message.Contains("Failed to consult cache for user-selected name One Piece"));
    }

    [Fact]
    public async Task ResolveAsync_FolderRecordVisibleOnlyAfterTransientFailures_ResolvesViaLocalizedTitleBackref()
    {
        _settings.DefaultPreferredLanguage = "en";
        SetupGetAsyncSequence(
            "kaiju",
            () => throw new InvalidOperationException("transient 1"),
            () => throw new InvalidOperationException("transient 2"),
            () => new SeriesMetadataCacheRecord
            {
                NormalizedKey = "kaiju",
                CanonicalTitle = "Kaiju No. 8",
                LookupStatus = "success",
                PreferredLanguage = "ja",
                LocalizedTitles =
                {
                    new LocalizedTitle(" ", "en"),
                    new LocalizedTitle("Kaiju No. 8", "en"),
                    new LocalizedTitle("怪獣8号", "ja")
                }
            });
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            "/library/Kaiju/c1.cbz",
            new ComicMetadata { Series = "怪獣8号" },
            mutateCache: true);

        Assert.Equal(SeriesNameResolutionStep.LocalizedTitleBackref, result.WinningStep);
        Assert.Equal("怪獣8号", result.ResolvedSeries);
        Assert.Equal("ja", result.AppliedLanguage);
        Assert.Equal("kaiju", result.MatchedCacheKey);
        Assert.Contains("matched localized-title on folder's cache record 'kaiju'", result.Explanation);
        Assert.Contains(_logger.Entries, e => e.Message.Contains("Failed to consult cache for user-selected name Kaiju"));
        Assert.Contains(_logger.Entries, e => e.Message.Contains("Failed to consult cache for matched title Kaiju"));
        _cache.Verify(c => c.SetUserAliasesAsync(
            "Kaiju No. 8",
            It.Is<IEnumerable<string>>(a => a.SequenceEqual(new[] { "Kaiju" })),
            It.IsAny<CancellationToken>()), Times.Once);
        _external.Verify(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("en")]
    public async Task ResolveAsync_LocalizedBackrefWithoutSeriesLanguage_AppliesGlobalLanguage(string? globalLanguage)
    {
        _settings.DefaultPreferredLanguage = globalLanguage;
        SetupGetAsyncSequence(
            "kaiju",
            () => null,
            () => null,
            () => new SeriesMetadataCacheRecord
            {
                NormalizedKey = "kaiju",
                CanonicalTitle = "Kaiju No. 8",
                LookupStatus = "manual",
                LocalizedTitles = { new LocalizedTitle("怪獣8号", "ja") }
            });
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            "/library/Kaiju/c1.cbz",
            new ComicMetadata { Series = "怪獣8号" },
            mutateCache: false);

        Assert.Equal(SeriesNameResolutionStep.LocalizedTitleBackref, result.WinningStep);
        Assert.Equal("Kaiju No. 8", result.ResolvedSeries);
        Assert.Equal(globalLanguage, result.AppliedLanguage);
        VerifyNoAliasWrites();
    }

    [Fact]
    public async Task ResolveAsync_LocalizedBackrefOnPinnedFolderRecord_ReportsUserSelectedName()
    {
        // The matched lookup for the folder misses; the user-selected lookup
        // (the fallback of step 3) finds a pinned record whose localized titles
        // contain the file's <Series>.
        SetupGetAsyncSequence(
            "kaiju",
            () => null,
            () => null,
            () => null,
            () => new SeriesMetadataCacheRecord
            {
                NormalizedKey = "kaiju",
                CanonicalTitle = "Kaiju No. 8",
                SeriesName = "Kaiju Pinned",
                LocalizedTitles = { new LocalizedTitle("怪獣8号", "ja") }
            });
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            "/library/Kaiju/c1.cbz",
            new ComicMetadata { Series = "怪獣8号" },
            mutateCache: false);

        Assert.Equal(SeriesNameResolutionStep.UserSelectedName, result.WinningStep);
        Assert.Equal("Kaiju Pinned", result.ResolvedSeries);
        Assert.Null(result.AppliedLanguage);
        Assert.Contains("has user-selected series name 'Kaiju Pinned'", result.Explanation);
    }

    [Theory]
    [InlineData("null-list")]
    [InlineData("empty-list")]
    [InlineData("no-match")]
    public async Task ResolveAsync_FolderRecordWithoutMatchingLocalizedTitle_FallsThroughToExistingSeries(string shape)
    {
        var folderRecord = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "kaiju",
            CanonicalTitle = "Kaiju No. 8",
            LookupStatus = "success"
        };
        folderRecord.LocalizedTitles = shape switch
        {
            "null-list" => null!,
            "empty-list" => new List<LocalizedTitle>(),
            _ => new List<LocalizedTitle> { null!, new(" ", "ja"), new("Something Else", "en") }
        };
        SetupGetAsyncSequence("kaiju", () => null, () => null, () => folderRecord);
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            "/library/Kaiju/c1.cbz",
            new ComicMetadata { Series = "怪獣8号" },
            mutateCache: false);

        Assert.Equal(SeriesNameResolutionStep.ExistingMetadata, result.WinningStep);
        Assert.Equal("怪獣8号", result.ResolvedSeries);
        // Neither candidate is "cleared", so both were offered to the external provider.
        _external.Verify(e => e.LookupSeriesAsync("Kaiju", It.IsAny<CancellationToken>()), Times.Once);
        _external.Verify(e => e.LookupSeriesAsync("怪獣8号", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_CacheAlwaysFails_LogsEachStepAndFallsBackToExternalLookup()
    {
        _cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("cache offline"));
        _external.Setup(e => e.LookupSeriesAsync("One Piece", It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "One Piece", Source = "MangaDex" });
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            "/library/One Piece/c1.cbz",
            new ComicMetadata { Series = "One Piece" },
            mutateCache: true);

        Assert.Equal(SeriesNameResolutionStep.ExternalLookup, result.WinningStep);
        Assert.Equal("One Piece", result.ResolvedSeries);
        Assert.Null(result.MatchedCacheKey);
        var warnings = _logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        Assert.Contains(warnings, m => m.Contains("Failed to consult cache for user-selected name One Piece"));
        Assert.Contains(warnings, m => m.Contains("Failed to consult cache for matched title One Piece"));
        Assert.Contains(warnings, m => m.Contains("Failed to consult cache for cleared-status check on One Piece"));
        // Folder name equals the resolved title, so no alias needs adding.
        VerifyNoAliasWrites();
    }

    [Fact]
    public async Task ResolveAsync_PersistingExternalLookupFails_ResolvesFromLookupAndLogsWarning()
    {
        _settings.DefaultPreferredLanguage = "ja";
        _external.Setup(e => e.LookupSeriesAsync("OP", It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ExternalSeriesMetadata
                 {
                     CanonicalTitle = "  One Piece  ",
                     Source = "AniList",
                     LocalizedTitles = new List<LocalizedTitle>
                     {
                         null!,
                         new(" ", "ja"),
                         new("ワンピース", "ja"),
                         new("One Piece", "en")
                     }
                 });
        _cache.Setup(c => c.PersistExternalLookupAsync("OP", It.IsAny<ExternalSeriesMetadata>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new IOException("disk full"));
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync("/library/OP/c1.cbz", metadata: null, mutateCache: true);

        Assert.Equal(SeriesNameResolutionStep.ExternalLookup, result.WinningStep);
        Assert.Equal("ワンピース", result.ResolvedSeries);
        Assert.Null(result.MatchedCacheKey);
        Assert.Equal("ja", result.AppliedLanguage);
        Assert.Contains("External provider 'AniList' returned canonical 'One Piece' for candidate 'OP'", result.Explanation);
        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Exception is IOException
            && e.Message.Contains("Failed to persist external lookup for series OP"));
        // The folder is still recorded as an alias of the trimmed canonical title.
        _cache.Verify(c => c.SetUserAliasesAsync(
            "One Piece",
            It.Is<IEnumerable<string>>(a => a.SequenceEqual(new[] { "OP" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_ExternalLookupWithoutSourceOrLanguage_UsesPersistedKeyAndPlaceholders()
    {
        _settings.DefaultPreferredLanguage = null;
        var lookup = new ExternalSeriesMetadata { CanonicalTitle = "One Piece", Source = null! };
        _external.Setup(e => e.LookupSeriesAsync("One Piece", It.IsAny<CancellationToken>())).ReturnsAsync(lookup);
        _cache.Setup(c => c.PersistExternalLookupAsync("One Piece", lookup, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new SeriesMetadataCacheRecord
              {
                  NormalizedKey = "one-piece",
                  CanonicalTitle = "One Piece",
                  LookupStatus = "success"
              });
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            "/library/One Piece/c1.cbz",
            new ComicMetadata { Series = "One Piece" },
            mutateCache: true);

        Assert.Equal(SeriesNameResolutionStep.ExternalLookup, result.WinningStep);
        Assert.Equal("one-piece", result.MatchedCacheKey);
        Assert.Null(result.AppliedLanguage);
        Assert.Contains("External provider '(unknown)'", result.Explanation);
        Assert.Contains("under language '(none)'", result.Explanation);
    }

    [Fact]
    public async Task ResolveAsync_ExternalLookupThrows_LogsWarningAndPreservesExistingSeries()
    {
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new HttpRequestException("provider down"));
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            "/library/Some Series/c1.cbz",
            new ComicMetadata { Series = " Existing Name " },
            mutateCache: true);

        Assert.Equal(SeriesNameResolutionStep.ExistingMetadata, result.WinningStep);
        Assert.Equal("Existing Name", result.ResolvedSeries);
        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Exception is HttpRequestException
            && e.Message.Contains("Failed to resolve external metadata for series Some Series"));
    }

    [Theory]
    [InlineData("user-selected")]
    [InlineData("matched")]
    [InlineData("cleared-check")]
    [InlineData("external")]
    [InlineData("persist")]
    [InlineData("folder-alias")]
    public async Task ResolveAsync_CallerCancelsMidResolution_ThrowsInsteadOfFallingBack(string cancelledAt)
    {
        using var cts = new CancellationTokenSource();
        SeriesMetadataCacheRecord? Cancel()
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }
        // "kaibutsu" is read by the user-selected step, the matched step and then the cleared-status check.
        switch (cancelledAt)
        {
            case "user-selected":
                SetupGetAsyncSequence("kaibutsu", Cancel);
                break;
            case "matched":
                SetupGetAsyncSequence("kaibutsu", () => null, Cancel);
                break;
            case "cleared-check":
                SetupGetAsyncSequence("kaibutsu", () => null, () => null, Cancel);
                break;
            case "persist":
                _cache.Setup(c => c.PersistExternalLookupAsync("Kaibutsu", It.IsAny<ExternalSeriesMetadata>(), It.IsAny<CancellationToken>()))
                      .Callback(() => cts.Cancel())
                      .ThrowsAsync(new OperationCanceledException(cts.Token));
                break;
            case "folder-alias":
                SetupGetAsyncSequence("kaiju-no-8", Cancel);
                break;
        }
        var lookup = _external.Setup(e => e.LookupSeriesAsync("Kaibutsu", It.IsAny<CancellationToken>()));
        if (cancelledAt == "external")
        {
            lookup.Callback(() => cts.Cancel()).ThrowsAsync(new OperationCanceledException(cts.Token));
        }
        else
        {
            lookup.ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "Kaiju No. 8", Source = "AniList" });
        }
        var resolver = CreateResolver();

        // Falling back to "Kaibutsu" would let the rename pipeline move the file to that name.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(
            "/library/Kaibutsu/c1.cbz",
            new ComicMetadata { Series = "Kaibutsu" },
            mutateCache: true,
            cts.Token));

        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Warning);
        VerifyNoAliasWrites();
        if (cancelledAt is "user-selected" or "matched" or "cleared-check")
        {
            _external.Verify(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task ResolveAsync_ExternalLookupWithBlankCanonical_IsIgnored()
    {
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "   ", Source = "ComicVine" });
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync("/library/Some Series/c1.cbz", metadata: null, mutateCache: true);

        Assert.Equal(SeriesNameResolutionStep.FolderName, result.WinningStep);
        Assert.Equal("Some Series", result.ResolvedSeries);
        _cache.Verify(c => c.PersistExternalLookupAsync(
            It.IsAny<string>(), It.IsAny<ExternalSeriesMetadata>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_WithoutCacheOrExternalProvider_FallsBackToFileAndFolderNames()
    {
        var resolver = CreateResolver(withCache: false, withExternal: false);

        var fromFolder = await resolver.ResolveAsync("/library/Some Series/c1.cbz", metadata: null);
        var fromMetadata = await resolver.ResolveAsync(
            "/library/Some Series/c1.cbz",
            new ComicMetadata { Series = "Other Series" });

        Assert.Equal(SeriesNameResolutionStep.FolderName, fromFolder.WinningStep);
        Assert.Equal("Some Series", fromFolder.ResolvedSeries);
        Assert.Contains("Fell back to folder-derived series 'Some Series'", fromFolder.Explanation);
        Assert.Equal(SeriesNameResolutionStep.ExistingMetadata, fromMetadata.WinningStep);
        Assert.Equal("Other Series", fromMetadata.ResolvedSeries);
        Assert.Equal(new[] { "Some Series", "Other Series" }, fromMetadata.Candidates);
    }

    [Fact]
    public async Task ResolveAsync_BlankFolderAndSeries_ReturnsUnknownSentinel()
    {
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            "/library/   /c1.cbz",
            new ComicMetadata { Series = "  " },
            mutateCache: true);

        Assert.Equal(SeriesNameResolutionStep.UnknownSentinel, result.WinningStep);
        Assert.Equal("Unknown Series", result.ResolvedSeries);
        Assert.Empty(result.Candidates);
        Assert.Contains("'Unknown Series' sentinel", result.Explanation);
        _cache.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _external.Verify(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("c1.cbz")]
    [InlineData("/c1.cbz")]
    public async Task ResolveAsync_FileWithoutParentFolderName_UsesUnknownSeriesFolder(string path)
    {
        var resolver = CreateResolver(withCache: false, withExternal: false);

        var result = await resolver.ResolveAsync(path, metadata: null);

        Assert.Equal("Unknown Series", result.FolderSeries);
        Assert.Equal("Unknown Series", result.ResolvedSeries);
    }

    [Fact]
    public void ResolveForRecord_BlankCanonicalWithoutPinnedName_ReturnsUnknownSeries()
    {
        var resolver = CreateResolver();

        Assert.Equal("Unknown Series", resolver.ResolveForRecord(new SeriesMetadataCacheRecord { CanonicalTitle = "  " }));
    }

    private sealed class ListLogger : ILogger<SeriesNameResolver>
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }
}
