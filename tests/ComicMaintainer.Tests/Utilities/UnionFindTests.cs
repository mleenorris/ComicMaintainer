using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class UnionFindTests
{
    [Fact]
    public void Find_RegistersAnUnknownValueAsItsOwnRepresentative()
    {
        var sets = new UnionFind<string>(StringComparer.OrdinalIgnoreCase);
        Assert.False(sets.Contains("Batman"));

        Assert.Equal("Batman", sets.Find("Batman"));

        Assert.True(sets.Contains("BATMAN"));
        Assert.Equal("Batman", sets.Find("batman"));
    }

    [Fact]
    public void Union_CollapsesChainsIntoOneSetWithoutMergingUnrelatedValues()
    {
        var sets = new UnionFind<string>(StringComparer.Ordinal);
        sets.Union("a", "b");
        sets.Union("c", "d");
        sets.Union("b", "c");
        sets.Union("a", "d");
        sets.Add("e");

        var root = sets.Find("a");

        Assert.All(new[] { "b", "c", "d" }, value => Assert.Equal(root, sets.Find(value)));
        Assert.Equal("e", sets.Find("e"));
        Assert.NotEqual(root, sets.Find("e"));
    }
}
