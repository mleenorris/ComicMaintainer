using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services;

public class SeriesDisplayTitleResolverTests
{
    private static SeriesMetadataCacheRecord BuildRecord(
        string canonical,
        string? preferredLanguage = null,
        string? seriesName = null,
        params (string title, string? lang)[] localized)
    {
        return new SeriesMetadataCacheRecord
        {
            NormalizedKey = "key",
            CanonicalTitle = canonical,
            PreferredLanguage = preferredLanguage,
            SeriesName = seriesName,
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
            localized: new (string, string?)[]
            {
                ("One Piece", "en"),
                ("ワンピース", "ja"),
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
            localized: new (string, string?)[]
            {
                ("Solo Leveling", "en"),
                ("Soro Reberingu", "ja-Latn")
            });

        Assert.Equal("Soro Reberingu", SeriesDisplayTitleResolver.Resolve(record, null));
    }

    [Fact]
    public void FallsBack_ToCanonical_WhenNoMatchFound()
    {
        var record = BuildRecord(
            canonical: "Kingdom",
            preferredLanguage: "zh",
            localized: new (string, string?)[]
            {
                ("Kingdom", "en"),
                ("キングダム", "ja")
            });

        Assert.Equal("Kingdom", SeriesDisplayTitleResolver.Resolve(record, null));
    }

    [Fact]
    public void SeriesName_AlwaysWins_OverLanguagePreference()
    {
        var record = BuildRecord(
            canonical: "My Preferred Title",
            preferredLanguage: "ja",
            seriesName: "My Preferred Title",
            localized: new (string, string?)[]
            {
                ("English Title", "en"),
                ("日本語タイトル", "ja")
            });

        Assert.Equal("My Preferred Title", SeriesDisplayTitleResolver.Resolve(record, null));
    }

    [Fact]
    public void GlobalDefaultLanguage_AppliesWhen_PerSeriesPreferenceIsMissing()
    {
        var record = BuildRecord(
            canonical: "Naruto",
            preferredLanguage: null,
            localized: new (string, string?)[]
            {
                ("Naruto", "en"),
                ("ナルト", "ja")
            });

        Assert.Equal("ナルト", SeriesDisplayTitleResolver.Resolve(record, globalDefaultLanguage: "ja"));
    }

    [Fact]
    public void PerSeriesPreference_OverridesGlobalDefault()
    {
        var record = BuildRecord(
            canonical: "Naruto",
            preferredLanguage: "en",
            localized: new (string, string?)[]
            {
                ("Naruto", "en"),
                ("ナルト", "ja")
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
            localized: new (string, string?)[]
            {
                ("Some Title", "en"),
                ("Untagged Synonym", null)
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
    public void SeriesName_WinsOverLanguagePreference()
    {
        // The user has selected the romaji variant for this series even
        // though their per-series preference would otherwise pick the
        // native Japanese title. The selected name wins.
        var record = BuildRecord(
            canonical: "One Piece",
            preferredLanguage: "ja",
            localized: new (string, string?)[]
            {
                ("One Piece", "en"),
                ("ワンピース", "ja"),
                ("Wan Piisu", "ja-Latn")
            });
        record.SeriesName = "Wan Piisu";

        Assert.Equal("Wan Piisu", SeriesDisplayTitleResolver.Resolve(record, globalDefaultLanguage: null));
    }

    [Fact]
    public void SeriesName_WinsOverGlobalDefaultLanguage()
    {
        var record = BuildRecord(
            canonical: "Naruto",
            preferredLanguage: null,
            localized: new (string, string?)[]
            {
                ("Naruto", "en"),
                ("ナルト", "ja")
            });
        record.SeriesName = "Naruto";

        // Global default would select the Japanese title; SeriesName overrides.
        Assert.Equal("Naruto", SeriesDisplayTitleResolver.Resolve(record, globalDefaultLanguage: "ja"));
    }

    [Fact]
    public void SeriesName_WinsOverLanguagePreference_WhenCanonicalAlsoDiffers()
    {
        // SeriesName is the single explicit user-selected display name and
        // wins over all automatic language-preference rules.
        var record = BuildRecord(
            canonical: "My Preferred Title",
            preferredLanguage: "ja",
            seriesName: "My Preferred Title",
            localized: new (string, string?)[]
            {
                ("English Title", "en"),
                ("日本語タイトル", "ja")
            });
        record.SeriesName = "My Preferred Title";

        Assert.Equal("My Preferred Title", SeriesDisplayTitleResolver.Resolve(record, null));
    }
}
