using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Behavioural tests for the PR-1-of-3 single write-side helper that keeps
/// <see cref="SeriesMetadataCacheEntity.SeriesName"/> consistent with the
/// record's inputs.
/// </summary>
public class SeriesNameDefaulterTests
{
    private static SeriesMetadataCacheEntity BuildEntity(
        string canonical,
        string? preferredLanguage = null,
        bool isUserCanonical = false,
        string? pinned = null)
    {
        return new SeriesMetadataCacheEntity
        {
            NormalizedKey = "key",
            CanonicalTitle = canonical,
            IsUserCanonical = isUserCanonical,
            PreferredLanguage = preferredLanguage,
            PinnedLocalizedTitle = pinned
        };
    }

    private static List<LocalizedTitle> Localized(params (string title, string? lang)[] entries)
        => entries.Select(e => new LocalizedTitle(e.title, e.lang)).ToList();

    [Fact]
    public void Recompute_Picks_LanguageMatch_From_LocalizedTitles()
    {
        var entity = BuildEntity("One Piece", preferredLanguage: "ja");
        var localized = Localized(("One Piece", "en"), ("ワンピース", "ja"));

        SeriesNameDefaulter.Recompute(entity, globalDefaultLanguage: null, localized);

        Assert.Equal("ワンピース", entity.SeriesName);
        Assert.Equal(SeriesNameSource.LanguageDefault, entity.SeriesNameSource);
        Assert.Equal("ja", entity.SeriesNameLanguage);
    }

    [Fact]
    public void Recompute_FallsBack_ToCanonical_WhenNoLanguageMatch()
    {
        var entity = BuildEntity("Kingdom", preferredLanguage: "zh");
        var localized = Localized(("Kingdom", "en"));

        SeriesNameDefaulter.Recompute(entity, globalDefaultLanguage: null, localized);

        Assert.Equal("Kingdom", entity.SeriesName);
        Assert.Equal(SeriesNameSource.LanguageDefault, entity.SeriesNameSource);
        Assert.Null(entity.SeriesNameLanguage);
    }

    [Fact]
    public void Recompute_Uses_GlobalDefault_WhenPerSeriesLanguageIsMissing()
    {
        var entity = BuildEntity("Naruto", preferredLanguage: null);
        var localized = Localized(("Naruto", "en"), ("ナルト", "ja"));

        SeriesNameDefaulter.Recompute(entity, globalDefaultLanguage: "ja", localized);

        Assert.Equal("ナルト", entity.SeriesName);
    }

    [Fact]
    public void Recompute_DoesNotOverwrite_UserSelected()
    {
        var entity = BuildEntity("One Piece", preferredLanguage: "ja");
        entity.SeriesName = "Wan Piisu";
        entity.SeriesNameSource = SeriesNameSource.UserSelected;

        SeriesNameDefaulter.Recompute(entity, globalDefaultLanguage: "ja",
            Localized(("One Piece", "en"), ("ワンピース", "ja")));

        // Sticky: user's pick stays.
        Assert.Equal("Wan Piisu", entity.SeriesName);
        Assert.Equal(SeriesNameSource.UserSelected, entity.SeriesNameSource);
    }

    [Fact]
    public void Recompute_PromotesLegacy_IsUserCanonical_ToUserSelected()
    {
        var entity = BuildEntity("My Preferred Title", isUserCanonical: true);

        SeriesNameDefaulter.Recompute(entity, globalDefaultLanguage: null, Localized());

        Assert.Equal("My Preferred Title", entity.SeriesName);
        Assert.Equal(SeriesNameSource.UserSelected, entity.SeriesNameSource);
    }

    [Fact]
    public void Recompute_PromotesLegacy_PinnedLocalizedTitle_ToUserSelected()
    {
        var entity = BuildEntity("One Piece", preferredLanguage: "ja", pinned: "Wan Piisu");

        SeriesNameDefaulter.Recompute(entity, globalDefaultLanguage: null,
            Localized(("One Piece", "en"), ("ワンピース", "ja"), ("Wan Piisu", "ja-Latn")));

        Assert.Equal("Wan Piisu", entity.SeriesName);
        Assert.Equal(SeriesNameSource.UserSelected, entity.SeriesNameSource);
    }

    [Fact]
    public void Recompute_BlankCanonical_FallsBack_ToNormalizedKey_AsFolderName()
    {
        var entity = new SeriesMetadataCacheEntity { NormalizedKey = "my-folder", CanonicalTitle = "" };

        SeriesNameDefaulter.Recompute(entity, globalDefaultLanguage: null, Localized());

        Assert.Equal("my-folder", entity.SeriesName);
        Assert.Equal(SeriesNameSource.FolderName, entity.SeriesNameSource);
    }
}
