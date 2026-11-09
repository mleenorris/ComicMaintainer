using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Tests.Models;

/// <summary>
/// The text shown to users is built from the failure's kind, never from its message, which
/// can carry an inner exception's text or the provider's own error wording.
/// </summary>
public class ExternalMetadataUnavailableExceptionTests
{
    private const string RawText = "secret api.internal:443";

    [Theory]
    [InlineData(ExternalMetadataFailureKind.Unavailable, "ComicVine could not be reached or answered with an error")]
    [InlineData(ExternalMetadataFailureKind.Timeout, "ComicVine did not answer in time")]
    [InlineData(ExternalMetadataFailureKind.RateLimited, "ComicVine is limiting requests; try again later")]
    [InlineData(ExternalMetadataFailureKind.InvalidResponse, "ComicVine returned a response that could not be read")]
    [InlineData(ExternalMetadataFailureKind.Configuration, "ComicVine rejected the request; check its settings")]
    [InlineData(ExternalMetadataFailureKind.Paced, "ComicVine is at its request pace limit; try again shortly")]
    public void UserMessage_NamesTheProviderAndKindButNotTheRawText(ExternalMetadataFailureKind kind, string expected)
    {
        var failure = new ExternalMetadataUnavailableException("ComicVine", kind, RawText);

        Assert.Equal(expected, failure.UserMessage);
        Assert.Equal(RawText, failure.Message);
    }

    [Fact]
    public void UserMessage_RoundsAProvidersRetryDelayUp()
    {
        var failure = new ExternalMetadataUnavailableException(
            "ComicVine", ExternalMetadataFailureKind.RateLimited, RawText, TimeSpan.FromSeconds(89.2));

        Assert.Equal("ComicVine is limiting requests; try again in 90s", failure.UserMessage);
    }

    [Fact]
    public void UserMessage_IgnoresAZeroRetryDelay()
    {
        var failure = new ExternalMetadataUnavailableException(
            "ComicVine", ExternalMetadataFailureKind.RateLimited, RawText, TimeSpan.Zero);

        Assert.Equal("ComicVine is limiting requests; try again later", failure.UserMessage);
    }

    [Fact]
    public void UserMessage_OfAnAggregateListsEachProviderWithoutTheRawText()
    {
        var failure = new ExternalMetadataUnavailableException(new[]
        {
            new ExternalMetadataUnavailableException("MangaDex", ExternalMetadataFailureKind.Timeout, RawText),
            new ExternalMetadataUnavailableException("ComicVine", ExternalMetadataFailureKind.Configuration, RawText),
        });

        Assert.Equal(
            "External metadata providers unavailable: MangaDex did not answer in time; ComicVine rejected the request; check its settings",
            failure.UserMessage);
        Assert.Contains(RawText, failure.Message);
    }
}
