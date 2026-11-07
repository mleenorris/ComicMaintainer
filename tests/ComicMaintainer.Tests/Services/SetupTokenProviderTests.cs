using ComicMaintainer.Core.Services.Auth;

namespace ComicMaintainer.Tests.Services;

public class SetupTokenProviderTests
{
    [Fact]
    public void AGeneratedCodeIsFourGroupsOfFiveBase32Characters()
    {
        var provider = new SetupTokenProvider(null);

        Assert.False(provider.IsConfigured);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{5}(-[0-9A-HJKMNP-TV-Z]{5}){3}$", provider.Token);
        Assert.NotEqual(provider.Token, new SetupTokenProvider(null).Token);
    }

    [Fact]
    public void AGeneratedCodeMatchesHowEverItIsRetyped()
    {
        var provider = new SetupTokenProvider(null);
        var code = provider.Token;

        Assert.True(provider.Matches(code));
        Assert.True(provider.Matches(code.ToLowerInvariant()));
        Assert.True(provider.Matches(code.Replace("-", string.Empty)));
        Assert.True(provider.Matches(" " + code.Replace("-", " ") + " "));
        // Crockford's base32 reads O as 0 and I or L as 1.
        Assert.True(provider.Matches(code.Replace('0', 'O').Replace('1', 'I')));
        var lastCharacter = code[^1] == 'Z' ? 'Y' : 'Z';
        Assert.False(provider.Matches(code[..^1] + lastCharacter));
        Assert.False(provider.Matches(code + "0"));
        Assert.False(provider.Matches(null));
        Assert.False(provider.Matches("   "));
    }

    [Fact]
    public void AConfiguredCodeMustMatchExactly()
    {
        var provider = new SetupTokenProvider("  my-Own-Setup-Code  ");

        Assert.True(provider.IsConfigured);
        Assert.Equal("my-Own-Setup-Code", provider.Token);
        Assert.True(provider.Matches("my-Own-Setup-Code"));
        Assert.False(provider.Matches("MY-OWN-SETUP-CODE"));
        Assert.False(provider.Matches("myOwnSetupCode"));
    }
}
