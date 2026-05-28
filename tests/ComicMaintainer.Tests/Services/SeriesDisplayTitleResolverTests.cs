using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services;

public class SeriesDisplayTitleResolverTests
{
    private static SeriesMetadataCacheRecord BuildRecord(
        string canonical,
        string? preferredLanguage = null,
        bool isUserCanonical = false,
        params (string title, string? lang)[] localized)
    {
        return new SeriesMetadataCacheRecord
        {
            NormalizedKey = "key",
            CanonicalTitle = canonical,
            PreferredLanguage = preferredLanguage,
            IsUserCanonical = isUserCanonical,
            LocalizedTitles = localized
                .Select(t => new LocalizedTitle(t.title, t.lang))
                .ToList()
        };
    }

    [Fact]
    public void Returns_FirstMatchingLocalizedTitle_ForPerSeriesPreference()
    {
        var record = BuildRecord(
            canonical: "One Piece",
            preferredLanguage: "ja",
            localized: new[]
            {
                ("One Piece", (string?)"en"),
                ("ワンピース", (string?)"ja"),
                ("Wan Piisu", "ja-Latn")
            });

        Assert.Equal("ワンピース", SeriesDisplayTitleResolver.Resolve(record, globalDefaultLanguage: null));
    }

    [Fact]
    public void RomajiMatches_When_PreferenceIsJapanese_AndOnlyRomajiAvailable()
    {
        // ja-Latn shares the primary subtag "ja" so it should match a "ja" preference.
        var record = BuildRecord(
            canonical: "Solo Leveling",
            preferredLanguage: "ja",
            localized: new[]
            {
                ("Solo Leveling", (string?)"en"),
                ("Soro Reberingu", (string?)"ja-Latn")
            });

        Assert.Equal("Soro Reberingu", SeriesDisplayTitleResolver.Resolve(record, null));
    }

    [Fact]
    public void FallsBack_ToCanonical_WhenNoMatchFound()
    {
        var record = BuildRecord(
            canonical: "Kingdom",
            preferredLanguage: "zh",
            localized: new[]
            {
                ("Kingdom", (string?)"en"),
                ("キングダム", (string?)"ja")
            });

        Assert.Equal("Kingdom", SeriesDisplayTitleResolver.Resolve(record, null));
    }

    [Fact]
    public void UserCanonicalOverride_AlwaysWins_OverLanguagePreference()
    {
        var record = BuildRecord(
            canonical: "My Preferred Title",
            preferredLanguage: "ja",
            isUserCanonical: true,
            localized: new[]
            {
                ("English Title", (string?)"en"),
                ("日本語タイトル", (string?)"ja")
            });

        Assert.Equal("My Preferred Title", SeriesDisplayTitleResolver.Resolve(record, null));
    }

    [Fact]
    public void GlobalDefaultLanguage_AppliesWhen_PerSeriesPreferenceIsMissing()
    {
        var record = BuildRecord(
            canonical: "Naruto",
            preferredLanguage: null,
            localized: new[]
            {
                ("Naruto", (string?)"en"),
                ("ナルト", (string?)"ja")
            });

        Assert.Equal("ナルト", SeriesDisplayTitleResolver.Resolve(record, globalDefaultLanguage: "ja"));
    }

    [Fact]
    public void PerSeriesPreference_OverridesGlobalDefault()
    {
        var record = BuildRecord(
            canonical: "Naruto",
            preferredLanguage: "en",
            localized: new[]
            {
                ("Naruto", (string?)"en"),
                ("ナルト", (string?)"ja")
            });

        Assert.Equal("Naruto", SeriesDisplayTitleResolver.Resolve(record, globalDefaultLanguage: "ja"));
    }

    [Fact]
    public void UntaggedTitles_AreNeverAutoMatched_ByLanguagePreference()
    {
        // Synonyms / aliases with Language=null should not match any preference.
        var record = BuildRecord(
            canonical: "Some Title",
            preferredLanguage: "ja",
            localized: new[]
            {
                ("Some Title", (string?)"en"),
                ("Untagged Synonym", (string?)null)
            });

        Assert.Equal("Some Title", SeriesDisplayTitleResolver.Resolve(record, null));
    }

    [Fact]
    public void EmptyLocalizedTitles_FallsBackToCanonical()
    {
        var record = BuildRecord(
            canonical: "Fallback",
            preferredLanguage: "ja");

        Assert.Equal("Fallback", SeriesDisplayTitleResolver.Resolve(record, "en"));
    }

    [Fact]
    public void PinnedLocalizedTitle_WinsOverLanguagePreference()
    {
        // The user has pinned the romaji variant for this series even
        // though their per-series preference would otherwise pick the
        // native Japanese title. Pin wins.
        var record = BuildRecord(
            canonical: "One Piece",
            preferredLanguage: "ja",
            localized: new[]
            {
                ("One Piece", (string?)"en"),
                ("ワンピース", (string?)"ja"),
                ("Wan Piisu", "ja-Latn")
            });
        record.PinnedLocalizedTitle = "Wan Piisu";

        Assert.Equal("Wan Piisu", SeriesDisplayTitleResolver.Resolve(record, globalDefaultLanguage: null));
    }

    [Fact]
    public void PinnedLocalizedTitle_WinsOverGlobalDefaultLanguage()
    {
        var record = BuildRecord(
            canonical: "Naruto",
            preferredLanguage: null,
            localized: new[]
            {
                ("Naruto", (string?)"en"),
                ("ナルト", (string?)"ja")
            });
        record.PinnedLocalizedTitle = "Naruto";

        // Global default would select the Japanese title; pin overrides.
        Assert.Equal("Naruto", SeriesDisplayTitleResolver.Resolve(record, globalDefaultLanguage: "ja"));
    }

    [Fact]
    public void UserCanonicalOverride_StillWinsOverPinnedLocalizedTitle()
    {
        // IsUserCanonical is the absolute top of the precedence chain — the
        // pin is a refinement of the language-preference rule, not a
        // replacement for the explicit user-canonical override.
        var record = BuildRecord(
            canonical: "My Preferred Title",
            preferredLanguage: "ja",
            isUserCanonical: true,
            localized: new[]
            {
                ("English Title", (string?)"en"),
                ("日本語タイトル", (string?)"ja")
            });
        record.PinnedLocalizedTitle = "English Title";

        Assert.Equal("My Preferred Title", SeriesDisplayTitleResolver.Resolve(record, null));
    }
}
