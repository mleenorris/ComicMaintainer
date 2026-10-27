using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class EmailAddressUtilsTests
{
    [Theory]
    [InlineData("reader@example.com>")]
    [InlineData("<reader@example.com")]
    [InlineData("reader@example.com,other@example.com")]
    [InlineData("reader@example.com\tother")]
    [InlineData("reader@example.com(work)")]
    public void IsValid_RejectsAnythingButASingleBareMailbox(string value)
    {
        Assert.False(EmailAddressUtils.IsValid(value));
    }

    [Fact]
    public void ValidateOrThrow_ReturnsTheTrimmedAddress()
    {
        Assert.Equal("reader@example.com", EmailAddressUtils.ValidateOrThrow("  reader@example.com ", "address"));
    }

    [Fact]
    public void ValidateOrThrow_NamesTheOffendingParameter()
    {
        var ex = Assert.Throws<ArgumentException>(() => EmailAddressUtils.ValidateOrThrow("reader@example.com(work)", "address"));

        Assert.Equal("address", ex.ParamName);
    }
}
