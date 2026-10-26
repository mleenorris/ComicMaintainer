using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class GitHubNameValidatorTests
{
    [Theory]
    [InlineData("octocat")]
    [InlineData("a")]
    [InlineData("my-org")]
    [InlineData("Org-2-Name")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789abc")] // 39 characters: the maximum
    public void IsValidOwner_AcceptsGitHubLogins(string value)
    {
        Assert.True(GitHubNameValidator.IsValidOwner(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--hyphen")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789abcd")] // 40 characters
    [InlineData("has space")]
    [InlineData("under_score")]
    [InlineData("dot.name")]
    [InlineData("../../user/repos")]
    [InlineData("owner/repo")]
    public void IsValidOwner_RejectsAnythingThatIsNotAGitHubLogin(string? value)
    {
        Assert.False(GitHubNameValidator.IsValidOwner(value));
    }

    [Theory]
    [InlineData("ComicMaintainer")]
    [InlineData("my.repo_name-1")]
    [InlineData(".github")]
    [InlineData("a")]
    public void IsValidRepository_AcceptsGitHubRepositoryNames(string value)
    {
        Assert.True(GitHubNameValidator.IsValidRepository(value));
    }

    [Fact]
    public void IsValidRepository_AcceptsTheMaximumLength()
    {
        Assert.True(GitHubNameValidator.IsValidRepository(new string('r', 100)));
        Assert.False(GitHubNameValidator.IsValidRepository(new string('r', 101)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("owner/repo")]
    [InlineData("../user/repos")]
    [InlineData("repo name")]
    [InlineData("repo%2Fname")]
    [InlineData("repo?x=1")]
    public void IsValidRepository_RejectsValuesThatCouldEscapeTheApiPath(string? value)
    {
        Assert.False(GitHubNameValidator.IsValidRepository(value));
    }
}
