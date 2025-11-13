using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

/// <summary>
/// Tests for the NaturalStringComparer to ensure proper numeric sorting of filenames
/// </summary>
public class NaturalStringComparerTests
{
    private readonly NaturalStringComparer _comparer;

    public NaturalStringComparerTests()
    {
        _comparer = new NaturalStringComparer();
    }

    [Fact]
    public void Compare_WithNullValues_HandlesCorrectly()
    {
        // Arrange & Act
        var result1 = _comparer.Compare(null, null);
        var result2 = _comparer.Compare("a", null);
        var result3 = _comparer.Compare(null, "a");

        // Assert
        Assert.Equal(0, result1); // Both null are equal
        Assert.True(result2 > 0); // Non-null is greater than null
        Assert.True(result3 < 0); // Null is less than non-null
    }

    [Fact]
    public void Compare_WithSimpleNumericNames_SortsNumerically()
    {
        // Arrange
        var names = new List<string> { "page10.jpg", "page2.jpg", "page1.jpg", "page20.jpg" };

        // Act
        names.Sort(_comparer);

        // Assert
        Assert.Equal(new[] { "page1.jpg", "page2.jpg", "page10.jpg", "page20.jpg" }, names);
    }

    [Fact]
    public void Compare_WithZeroPaddedNumbers_SortsNumerically()
    {
        // Arrange
        var names = new List<string> { "page001.jpg", "page010.jpg", "page002.jpg", "page100.jpg" };

        // Act
        names.Sort(_comparer);

        // Assert
        Assert.Equal(new[] { "page001.jpg", "page002.jpg", "page010.jpg", "page100.jpg" }, names);
    }

    [Fact]
    public void Compare_WithMixedPadding_SortsNumerically()
    {
        // Arrange
        var names = new List<string> { "page1.jpg", "page01.jpg", "page001.jpg", "page10.jpg" };

        // Act
        names.Sort(_comparer);

        // Assert - All represent page 1 and page 10, so sorted by numeric value
        Assert.Equal("page1.jpg", names[0]);
        Assert.Equal("page01.jpg", names[1]);
        Assert.Equal("page001.jpg", names[2]);
        Assert.Equal("page10.jpg", names[3]);
    }

    [Fact]
    public void Compare_WithMultipleNumbers_ComparesEachSegment()
    {
        // Arrange
        var names = new List<string> { "vol2-page10.jpg", "vol1-page20.jpg", "vol2-page2.jpg" };

        // Act
        names.Sort(_comparer);

        // Assert
        Assert.Equal(new[] { "vol1-page20.jpg", "vol2-page2.jpg", "vol2-page10.jpg" }, names);
    }

    [Fact]
    public void Compare_WithTextOnly_SortsCaseInsensitively()
    {
        // Arrange
        var names = new List<string> { "zebra.jpg", "Apple.jpg", "banana.jpg" };

        // Act
        names.Sort(_comparer);

        // Assert
        Assert.Equal(new[] { "Apple.jpg", "banana.jpg", "zebra.jpg" }, names);
    }

    [Fact]
    public void Compare_WithCommonComicFileNames_SortsCorrectly()
    {
        // Arrange
        // Simulating a real comic with page files that might be named like this
        var names = new List<string>
        {
            "cover.jpg",
            "page14.jpg",
            "page1.jpg",
            "page2.jpg",
            "page3.jpg",
            "page10.jpg",
            "page11.jpg"
        };

        // Act
        names.Sort(_comparer);

        // Assert
        Assert.Equal(new[]
        {
            "cover.jpg",
            "page1.jpg",
            "page2.jpg",
            "page3.jpg",
            "page10.jpg",
            "page11.jpg",
            "page14.jpg"
        }, names);
    }

    [Fact]
    public void Compare_WithDifferentExtensions_SortsCorrectly()
    {
        // Arrange
        var names = new List<string> { "page10.png", "page2.jpg", "page1.webp" };

        // Act
        names.Sort(_comparer);

        // Assert
        Assert.Equal(new[] { "page1.webp", "page2.jpg", "page10.png" }, names);
    }

    [Fact]
    public void Compare_WithNestedPathsLikeInArchive_SortsCorrectly()
    {
        // Arrange
        var names = new List<string>
        {
            "folder/page14.jpg",
            "folder/page2.jpg",
            "folder/page1.jpg"
        };

        // Act
        names.Sort(_comparer);

        // Assert
        Assert.Equal(new[]
        {
            "folder/page1.jpg",
            "folder/page2.jpg",
            "folder/page14.jpg"
        }, names);
    }

    [Theory]
    [InlineData("page1.jpg", "page2.jpg", -1)] // page1 < page2
    [InlineData("page2.jpg", "page1.jpg", 1)]  // page2 > page1
    [InlineData("page1.jpg", "page1.jpg", 0)]  // Equal
    [InlineData("page1.jpg", "page10.jpg", -1)] // page1 < page10
    [InlineData("page10.jpg", "page2.jpg", 1)]  // page10 > page2
    public void Compare_WithSpecificPairs_ReturnsExpectedResult(string x, string y, int expected)
    {
        // Act
        var result = _comparer.Compare(x, y);

        // Assert
        if (expected < 0)
            Assert.True(result < 0, $"Expected {x} < {y}");
        else if (expected > 0)
            Assert.True(result > 0, $"Expected {x} > {y}");
        else
            Assert.Equal(0, result);
    }
}
