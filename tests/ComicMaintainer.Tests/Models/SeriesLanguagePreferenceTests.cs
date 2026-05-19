using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Tests.Models;

public class SeriesLanguagePreferenceTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData(" ja ", "ja")]
    [InlineData("ja-Latn", "ja")] // primary subtag extracted
    [InlineData("zh-hk", "zh")]
    [InlineData("zh-Hans", "zh")]
    public void Normalize_AcceptsAllowedLanguages(string input, string expected)
    {
        Assert.Equal(expected, SeriesLanguagePreference.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fr")]
    [InlineData("xx")]
    public void Normalize_RejectsUnknownLanguages(string? input)
    {
        Assert.Null(SeriesLanguagePreference.Normalize(input));
    }

    [Theory]
    [InlineData("ja", "ja-Latn", true)]
    [InlineData("ja", "ja", true)]
    [InlineData("zh", "zh-hk", true)]
    [InlineData("en", "ja", false)]
    [InlineData("ja", null, false)]
    [InlineData(null, "ja", false)]
    [InlineData("", "ja", false)]
    public void Matches_CompareesByPrimarySubtag(string? preferred, string? titleLang, bool expected)
    {
        Assert.Equal(expected, SeriesLanguagePreference.Matches(preferred, titleLang));
    }

    [Fact]
    public void ValidateOrThrow_AllowsNullAndEmpty()
    {
        Assert.Null(SeriesLanguagePreference.ValidateOrThrow(null));
        Assert.Null(SeriesLanguagePreference.ValidateOrThrow(""));
        Assert.Null(SeriesLanguagePreference.ValidateOrThrow("   "));
    }

    [Fact]
    public void ValidateOrThrow_NormalizesValidValues()
    {
        Assert.Equal("ja", SeriesLanguagePreference.ValidateOrThrow("JA"));
        Assert.Equal("zh", SeriesLanguagePreference.ValidateOrThrow("zh-hk"));
    }

    [Fact]
    public void ValidateOrThrow_ThrowsForUnsupportedValue()
    {
        Assert.Throws<ArgumentException>(() => SeriesLanguagePreference.ValidateOrThrow("fr"));
    }
}
