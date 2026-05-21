using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services;

public class SeriesDisplayTitleResolverTests
{
    private static SeriesMetadataCacheRecord BuildRecord(
        string canonical,
        string? preferredLanguage = null,
        bool isUserCanonical = false,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? userAliases = null,
        params (string title, string? lang)[] localized)
    {
        return new SeriesMetadataCacheRecord
        {
            NormalizedKey = "key",
            CanonicalTitle = canonical,
            PreferredLanguage = preferredLanguage,
            IsUserCanonical = isUserCanonical,
            Aliases = aliases?.ToList() ?? new List<string>(),
            UserAliases = userAliases?.ToList() ?? new List<string>(),
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
    public void EnglishPreference_PicksLatinAliasOverKoreanCanonical_WhenLocalizedTitlesEmpty()
    {
        // Reproduces the user-reported symptom: a Manhwa cached from a
        // Korean-primary provider record (Korean canonical, no localized
        // titles) but with an English alias and an "en" preference.
        var record = BuildRecord(
            canonical: "솔로 레벨링",
            preferredLanguage: "en",
            aliases: new[] { "Solo Leveling" });

        Assert.Equal("Solo Leveling", SeriesDisplayTitleResolver.Resolve(record, null));
    }

    [Fact]
    public void EnglishPreference_PrefersTaggedLocalizedTitle_OverLatinAlias()
    {
        var record = BuildRecord(
            canonical: "Some Series",
            preferredLanguage: "en",
            aliases: new[] { "Different English Alias" },
            localized: new[]
            {
                ("Official English Title", (string?)"en"),
                ("オフィシャル", (string?)"ja")
            });

        Assert.Equal("Official English Title", SeriesDisplayTitleResolver.Resolve(record, null));
    }

    [Fact]
    public void KoreanPreference_DoesNotAutoPickLatinAlias_FallsBackToCanonical()
    {
        // Latin-script heuristic must NOT satisfy a CJK preference, even when
        // no localized title matches. We expect the canonical to be returned
        // unchanged.
        var record = BuildRecord(
            canonical: "솔로 레벨링",
            preferredLanguage: "ko",
            aliases: new[] { "Solo Leveling" });

        Assert.Equal("솔로 레벨링", SeriesDisplayTitleResolver.Resolve(record, null));
    }

    [Fact]
    public void UserCanonicalOverride_AlwaysWins_OverAliases()
    {
        var record = BuildRecord(
            canonical: "솔로 레벨링",
            preferredLanguage: "en",
            isUserCanonical: true,
            aliases: new[] { "Solo Leveling" });

        Assert.Equal("솔로 레벨링", SeriesDisplayTitleResolver.Resolve(record, null));
    }

    [Fact]
    public void EnglishPreference_PrefersUserAliasOverProviderAlias()
    {
        var record = BuildRecord(
            canonical: "솔로 레벨링",
            preferredLanguage: "en",
            aliases: new[] { "Solo Leveling" },
            userAliases: new[] { "Only I Level Up" });

        Assert.Equal("Only I Level Up", SeriesDisplayTitleResolver.Resolve(record, null));
    }

    [Fact]
    public void EnglishPreference_FallsThroughToLatinCanonical_WhenAliasesAreCjk()
    {
        // No tagged English LocalizedTitle, both aliases are CJK, but the
        // canonical itself is Latin. The canonical should win on the
        // heuristic before we fall through to the unconditional fallback.
        var record = BuildRecord(
            canonical: "Solo Leveling",
            preferredLanguage: "en",
            aliases: new[] { "나 혼자만 레벨업" });

        Assert.Equal("Solo Leveling", SeriesDisplayTitleResolver.Resolve(record, null));
    }

    [Fact]
    public void EnglishPreference_SkipsAliasesWithCjkCharacters()
    {
        var record = BuildRecord(
            canonical: "솔로 레벨링",
            preferredLanguage: "en",
            aliases: new[] { "나 혼자만 레벨업", "Solo Leveling" });

        Assert.Equal("Solo Leveling", SeriesDisplayTitleResolver.Resolve(record, null));
    }

    [Fact]
    public void EnglishPreference_AcceptsLatinExtended_NotJustAscii()
    {
        // Romaji-with-macrons and similar Latin Extended characters should
        // still be treated as Latin script for the English heuristic.
        var record = BuildRecord(
            canonical: "솔로 레벨링",
            preferredLanguage: "en",
            aliases: new[] { "Tōkyō Ghoul" });

        Assert.Equal("Tōkyō Ghoul", SeriesDisplayTitleResolver.Resolve(record, null));
    }
}
