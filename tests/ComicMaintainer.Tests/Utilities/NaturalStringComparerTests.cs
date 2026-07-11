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

    [Fact]
    public void Compare_WithPureNumericFilenames_SortsNumerically()
    {
        // Arrange - Files named with just numbers (common in comics)
        var names = new List<string> { "14.jpg", "1.jpg", "2.jpg", "10.jpg", "3.jpg", "100.jpg" };

        // Act
        names.Sort(_comparer);

        // Assert
        Assert.Equal(new[] { "1.jpg", "2.jpg", "3.jpg", "10.jpg", "14.jpg", "100.jpg" }, names);
    }

    [Fact]
    public void Compare_WithZeroPaddedPureNumericFilenames_SortsNumerically()
    {
        // Arrange
        var names = new List<string> { "001.jpg", "010.jpg", "002.jpg", "100.jpg" };

        // Act
        names.Sort(_comparer);

        // Assert
        Assert.Equal(new[] { "001.jpg", "002.jpg", "010.jpg", "100.jpg" }, names);
    }

    [Fact]
    public void Compare_WithMixedNumericAndNamedPages_SortsCorrectly()
    {
        // Arrange - Mix of pure numbers and named pages
        var names = new List<string> { "14.jpg", "page1.jpg", "2.jpg", "cover.jpg", "10.jpg" };

        // Act
        names.Sort(_comparer);

        // Assert
        // Numbers come before "cover" (alphabetically), page files come last
        Assert.Equal(new[] { "2.jpg", "10.jpg", "14.jpg", "cover.jpg", "page1.jpg" }, names);
    }

    [Theory]
    [InlineData("1.jpg", "2.jpg", -1)]     // 1 < 2
    [InlineData("2.jpg", "1.jpg", 1)]      // 2 > 1
    [InlineData("1.jpg", "10.jpg", -1)]    // 1 < 10
    [InlineData("10.jpg", "2.jpg", 1)]     // 10 > 2
    [InlineData("14.jpg", "3.jpg", 1)]     // 14 > 3
    [InlineData("01.jpg", "1.jpg", 0)]     // 01 == 1 (numerically equal)
    [InlineData("001.jpg", "1.jpg", 0)]    // 001 == 1 (numerically equal)
    public void Compare_WithPureNumericPairs_ReturnsExpectedResult(string x, string y, int expected)
    {
        // Act
        var result = _comparer.Compare(x, y);

        // Assert
        if (expected < 0)
            Assert.True(result < 0, $"Expected {x} < {y}, got {result}");
        else if (expected > 0)
            Assert.True(result > 0, $"Expected {x} > {y}, got {result}");
        else
            Assert.Equal(0, result);
    }

    [Fact]
    public void Compare_WithVeryLargeNumbers_SortsNumericallyNotLexicographically()
    {
        // Digit runs far beyond Int32.MaxValue (2,147,483,647) and Int64.MaxValue
        // must still sort by numeric value. The previous int.TryParse based
        // implementation overflowed and silently fell back to lexicographic
        // ordering, which corrupted page/issue order for such names.
        var names = new List<string>
        {
            "20000000000.jpg",   // 2e10
            "3000000000.jpg",    // 3e9
            "160.jpg",
            "5.jpg",
            "99999999999999999999.jpg" // 1e20, overflows Int64
        };

        names.Sort(_comparer);

        Assert.Equal(new[]
        {
            "5.jpg",
            "160.jpg",
            "3000000000.jpg",
            "20000000000.jpg",
            "99999999999999999999.jpg"
        }, names);
    }

    [Theory]
    [InlineData("3000000000.jpg", "20000000000.jpg", -1)]  // 3e9 < 2e10
    [InlineData("20000000000.jpg", "3000000000.jpg", 1)]   // 2e10 > 3e9
    [InlineData("00000000009.jpg", "9.jpg", 0)]            // equal magnitude, zero-padded
    public void Compare_WithVeryLargeNumericPairs_ReturnsExpectedResult(string x, string y, int expected)
    {
        var result = _comparer.Compare(x, y);

        if (expected < 0)
            Assert.True(result < 0, $"Expected {x} < {y}, got {result}");
        else if (expected > 0)
            Assert.True(result > 0, $"Expected {x} > {y}, got {result}");
        else
            Assert.Equal(0, result);
    }
}
