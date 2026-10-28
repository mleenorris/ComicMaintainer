using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.Extensions.Primitives;

namespace ComicMaintainer.Tests.Infrastructure;

public class ConditionalRequestsTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("\"abc\"", true)]
    [InlineData("W/\"abc\"", true)]
    [InlineData("\"x\",\"abc\"", true)]
    [InlineData("\"x\", W/\"abc\"", true)]
    [InlineData("*", true)]
    [InlineData("\"abcd\"", false)]
    [InlineData("W/\"abcd\"", false)]
    [InlineData("\"x\", W/\"y\"", false)]
    [InlineData("abc", false)]
    public void IfNoneMatchSatisfied_ParsesHeader(string header, bool expected)
    {
        Assert.Equal(expected, ConditionalRequests.IfNoneMatchSatisfied(header, "\"abc\""));
    }

    [Fact]
    public void IfNoneMatchSatisfied_NoHeader_ReturnsFalse()
    {
        Assert.False(ConditionalRequests.IfNoneMatchSatisfied(StringValues.Empty, "\"abc\""));
    }

    [Theory]
    [InlineData("W/\"abc\"", true)]
    [InlineData("\"y\"", false)]
    public void IfNoneMatchSatisfied_ChecksEveryHeaderLine(string secondLine, bool expected)
    {
        var header = new StringValues(new[] { "\"x\"", secondLine });

        Assert.Equal(expected, ConditionalRequests.IfNoneMatchSatisfied(header, "\"abc\""));
    }
}
