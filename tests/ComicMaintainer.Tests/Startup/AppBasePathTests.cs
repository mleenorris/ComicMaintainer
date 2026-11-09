using ComicMaintainer.WebApi.Startup;

namespace ComicMaintainer.Tests.Startup;

public class AppBasePathTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("/", "")]
    [InlineData("//", "")]
    [InlineData("/comics", "/comics")]
    [InlineData("comics", "/comics")]
    [InlineData("/comics/", "/comics")]
    [InlineData("comics/", "/comics")]
    [InlineData("  /comics/  ", "/comics")]
    [InlineData("/apps/comics", "/apps/comics")]
    [InlineData("/my-comics_v2.1~x", "/my-comics_v2.1~x")]
    public void Normalize_GivesALeadingSlashAndNoTrailingSlash(string? configured, string expected)
    {
        Assert.Equal(expected, AppBasePath.Normalize(configured));
        Assert.False(AppBasePath.IsInvalid(configured));
    }

    [Theory]
    [InlineData("/com ics")]
    [InlineData("/comics\"><script>")]
    [InlineData("/comics?x=1")]
    [InlineData("/comics#x")]
    [InlineData("/a//b")]
    [InlineData("/../etc")]
    [InlineData("/a/./b")]
    [InlineData("/a/..")]
    [InlineData("/café")]
    [InlineData("comics\n/")]
    [InlineData("/comics\n/")]
    [InlineData("/co\nmics")]
    public void Normalize_RefusesWhatCouldNotBeWrittenIntoAUrl(string configured)
    {
        Assert.Equal(string.Empty, AppBasePath.Normalize(configured));
        Assert.True(AppBasePath.IsInvalid(configured));
    }

    [Theory]
    [InlineData("/comics", "<script src=\"/comics/js/x.js\"></script>")]
    [InlineData("/comics/", "<script src=\"/comics/js/x.js\"></script>")]
    [InlineData(null, "<script src=\"/js/x.js\"></script>")]
    public void Apply_FillsInEveryPlaceholder(string? configured, string expected)
    {
        Assert.Equal(expected, AppBasePath.Apply("<script src=\"__BASE_PATH__/js/x.js\"></script>", configured));
        Assert.Equal(expected + expected, AppBasePath.Apply(
            "<script src=\"__BASE_PATH__/js/x.js\"></script><script src=\"__BASE_PATH__/js/x.js\"></script>", configured));
    }
}
