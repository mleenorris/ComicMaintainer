using ComicMaintainer.Core.Services.Auth;

namespace ComicMaintainer.Tests.Authentication;

/// <summary>What counts as a generated API key where a field can hold either a key or a password.</summary>
public sealed class ApiKeysTests
{
    [Fact]
    public void EveryGeneratedKeyHasTheGeneratedForm()
    {
        for (var i = 0; i < 200; i++)
        {
            var key = ApiKeys.Generate();

            Assert.Equal(ApiKeys.GeneratedLength, key.Length);
            Assert.True(ApiKeys.HasGeneratedForm(key), key);
        }
    }

    public static TheoryData<string> OtherValues => new()
    {
        { string.Empty },
        { "cm_" },
        // One character short and one too long.
        { "cm_" + new string('a', 42) },
        { "cm_" + new string('a', 44) },
        // The right length without the prefix, or with it in another case.
        { "xx_" + new string('a', 43) },
        { "CM_" + new string('a', 43) },
        // A character base64url does not have.
        { "cm_" + new string('a', 42) + "!" },
        { "cm_" + new string('a', 42) + "=" },
        { "Passw0rdOk" },
    };

    [Theory]
    [MemberData(nameof(OtherValues))]
    public void AnythingElseDoesNot(string value)
    {
        Assert.False(ApiKeys.HasGeneratedForm(value));
    }
}
