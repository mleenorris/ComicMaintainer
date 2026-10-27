using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Tests.Models;

public class EmailDeliveryTests
{
    [Theory]
    [InlineData("original", true)]
    [InlineData("EPUB", true)]
    [InlineData("Azw3", true)]
    [InlineData("device", false)]
    [InlineData("pdf", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsConcreteFormat_AcceptsOnlyTheThreeFileFormats(string? value, bool expected)
    {
        Assert.Equal(expected, EmailDeliveryFormat.IsConcreteFormat(value));
    }

    [Theory]
    [InlineData(null, EmailDeliveryFormat.Device)]
    [InlineData("", EmailDeliveryFormat.Device)]
    [InlineData("   ", EmailDeliveryFormat.Device)]
    [InlineData(" Device ", EmailDeliveryFormat.Device)]
    [InlineData("ORIGINAL", EmailDeliveryFormat.Original)]
    [InlineData("epub", EmailDeliveryFormat.Epub)]
    [InlineData(" AZW3 ", EmailDeliveryFormat.Azw3)]
    public void NormalizeSubscriptionOrThrow_ReturnsTheCanonicalConstant(string? value, string expected)
    {
        Assert.Equal(expected, EmailDeliveryFormat.NormalizeSubscriptionOrThrow(value, "format"));
    }

    [Fact]
    public void NormalizeSubscriptionOrThrow_RejectsUnknownFormats()
    {
        var ex = Assert.Throws<ArgumentException>(() => EmailDeliveryFormat.NormalizeSubscriptionOrThrow("pdf", "deliveryFormat"));

        Assert.Equal("deliveryFormat", ex.ParamName);
        Assert.Contains("'pdf'", ex.Message);
        Assert.Contains("'device'", ex.Message);
    }

    [Theory]
    [InlineData("count", true)]
    [InlineData("ALL", true)]
    [InlineData("none", false)]
    [InlineData(null, false)]
    public void IsCondensing_IsTrueOnlyForCountAndAll(string? value, bool expected)
    {
        Assert.Equal(expected, EmailCondenseMode.IsCondensing(value));
    }

    [Theory]
    [InlineData(null, EmailCondenseMode.None)]
    [InlineData("", EmailCondenseMode.None)]
    [InlineData("  ", EmailCondenseMode.None)]
    [InlineData(" None ", EmailCondenseMode.None)]
    [InlineData("COUNT", EmailCondenseMode.Count)]
    [InlineData("all", EmailCondenseMode.All)]
    public void CondenseModeNormalizeOrThrow_ReturnsTheCanonicalConstant(string? value, string expected)
    {
        Assert.Equal(expected, EmailCondenseMode.NormalizeOrThrow(value, "mode"));
    }

    [Fact]
    public void CondenseModeNormalizeOrThrow_RejectsUnknownModes()
    {
        var ex = Assert.Throws<ArgumentException>(() => EmailCondenseMode.NormalizeOrThrow("weekly", "condenseMode"));

        Assert.Equal("condenseMode", ex.ParamName);
        Assert.Contains("'weekly'", ex.Message);
    }

    [Theory]
    [InlineData(EmailCondenseMode.None, null, 0, 1)]
    [InlineData(EmailCondenseMode.All, 3, 12, 12)]
    [InlineData(EmailCondenseMode.All, null, -5, 1)]
    // The mode comparison is ordinal: callers pass the normalized constant.
    [InlineData("COUNT", null, 7, 7)]
    public void NormalizeIssuesPerBookOrThrow_UsesTheWholeSelectionOutsideCountMode(string mode, int? perBook, int total, int expected)
    {
        Assert.Equal(expected, EmailCondenseMode.NormalizeIssuesPerBookOrThrow(mode, perBook, total, "issuesPerBook"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(EmailCondenseMode.MaxIssuesPerBook)]
    public void NormalizeIssuesPerBookOrThrow_AcceptsCountsWithinTheLimits(int perBook)
    {
        Assert.Equal(perBook, EmailCondenseMode.NormalizeIssuesPerBookOrThrow(EmailCondenseMode.Count, perBook, 1, "issuesPerBook"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(-4)]
    public void NormalizeIssuesPerBookOrThrow_RequiresAtLeastTwoIssuesWhenCondensingByCount(int? perBook)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            EmailCondenseMode.NormalizeIssuesPerBookOrThrow(EmailCondenseMode.Count, perBook, 10, "issuesPerBook"));

        Assert.Equal("issuesPerBook", ex.ParamName);
        Assert.Contains("at least 2", ex.Message);
    }

    [Fact]
    public void NormalizeIssuesPerBookOrThrow_RejectsMoreThanTheMaximumPerBook()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            EmailCondenseMode.NormalizeIssuesPerBookOrThrow(EmailCondenseMode.Count, EmailCondenseMode.MaxIssuesPerBook + 1, 1000, "issuesPerBook"));

        Assert.Equal("issuesPerBook", ex.ParamName);
        Assert.Contains("At most 500 issues", ex.Message);
    }

    [Fact]
    public void CondensePlan_CanEmailOnlyWhenThereAreBooksAndNoneIsOversized()
    {
        var fits = new CondensedBookDto("Book 1", new[] { "/a.cbz" }, 1_000, ExceedsAttachmentLimit: false);
        var oversized = new CondensedBookDto("Book 2", new[] { "/b.cbz" }, 9_000_000, ExceedsAttachmentLimit: true);
        var skipped = new Dictionary<string, string>();

        var empty = new CondensePlanDto(EmailCondenseMode.All, 0, 1, 5_000_000, Array.Empty<CondensedBookDto>(), skipped);
        var allFit = empty with { Books = new[] { fits } };
        var withOversized = empty with { Books = new[] { fits, oversized } };

        Assert.False(empty.CanEmail);
        Assert.Equal(0, empty.OversizedBookCount);
        Assert.True(allFit.CanEmail);
        Assert.False(withOversized.CanEmail);
        Assert.Equal(1, withOversized.OversizedBookCount);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(5, 0, 0)]
    [InlineData(1, 3, 33)]
    [InlineData(3, 3, 100)]
    [InlineData(7, 3, 100)]
    [InlineData(-2, 3, 0)]
    public void BuildProgressPercentage_IsClampedAndZeroWithoutPages(int completed, int total, int expected)
    {
        var progress = CondensedBookBuildProgress.Empty with { CompletedPages = completed, TotalPages = total };

        Assert.Equal(expected, progress.Percentage);
    }

    [Fact]
    public void BuildProgressPercentage_DoesNotOverflowForLargePageCounts()
    {
        var progress = CondensedBookBuildProgress.Empty with { CompletedPages = int.MaxValue - 1, TotalPages = int.MaxValue };

        Assert.Equal(99, progress.Percentage);
    }
}
