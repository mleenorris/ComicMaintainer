using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

/// <summary>
/// A naive title-caser treats an apostrophe as a word break ("Count’S"); only that capital,
/// on an English contraction ending, is lowered.
/// </summary>
public sealed class ApostropheCasingTests
{
    [Theory]
    [InlineData("Trash Of The Count’S Family", "Trash Of The Count’s Family")]
    [InlineData("Trash Of The Count'S Family", "Trash Of The Count's Family")]
    [InlineData("I’Ll Become A Villainess", "I’ll Become A Villainess")]
    [InlineData("The Swordmaster’S Son", "The Swordmaster’s Son")]
    [InlineData("Don'T Toy With Me", "Don't Toy With Me")]
    [InlineData("You'Re Mine, We'Ve Won, I'D Go, I'M Here", "You're Mine, We've Won, I'd Go, I'm Here")]
    [InlineData("Michael Turner’S", "Michael Turner’s")]
    public void Repair_TitleCasedContraction_LowersTheEnding(string name, string expected)
    {
        Assert.Equal(expected, ApostropheCasing.Repair(name));
    }

    [Theory]
    [InlineData("Trash of the Count’s Family")]
    [InlineData("O'Brien")]
    [InlineData("D'Artagnan")]
    [InlineData("Rock'N'Roll")]
    [InlineData("DON'T STOP")]
    [InlineData("I'LL BE BACK")]
    [InlineData("Ma'Sa")]
    [InlineData("90'S Kids")]
    [InlineData("Saga")]
    [InlineData("")]
    [InlineData(null)]
    public void Repair_AnythingElse_IsLeftAsItIs(string? name)
    {
        Assert.Equal(name, ApostropheCasing.Repair(name));
    }
}
