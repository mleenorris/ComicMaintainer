using ComicMaintainer.Core.Reader.Models;
using ComicMaintainer.Core.Reader.Services;

namespace ComicMaintainer.Tests.Reader;

public class ReadingProgressCalculatorTests
{
    // ─── CalculatePercentComplete ─────────────────────────────────────────────

    [Fact]
    public void CalculatePercentComplete_HalfwayThrough_Returns50()
    {
        var result = ReadingProgressCalculator.CalculatePercentComplete(5, 10);
        Assert.Equal(50.0, result);
    }

    [Fact]
    public void CalculatePercentComplete_AtLastPage_Returns100()
    {
        var result = ReadingProgressCalculator.CalculatePercentComplete(10, 10);
        Assert.Equal(100.0, result);
    }

    [Fact]
    public void CalculatePercentComplete_AtFirstPage_ReturnsExpected()
    {
        var result = ReadingProgressCalculator.CalculatePercentComplete(1, 10);
        Assert.Equal(10.0, result);
    }

    [Fact]
    public void CalculatePercentComplete_ZeroTotalPages_ReturnsZero()
    {
        var result = ReadingProgressCalculator.CalculatePercentComplete(1, 0);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void CalculatePercentComplete_NegativeTotalPages_ReturnsZero()
    {
        var result = ReadingProgressCalculator.CalculatePercentComplete(1, -5);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void CalculatePercentComplete_OverflowCurrentPage_ClampedTo100()
    {
        var result = ReadingProgressCalculator.CalculatePercentComplete(20, 10);
        Assert.Equal(100.0, result);
    }

    // ─── IsCompleted ──────────────────────────────────────────────────────────

    [Fact]
    public void IsCompleted_CurrentEqualToTotal_ReturnsTrue()
    {
        Assert.True(ReadingProgressCalculator.IsCompleted(10, 10));
    }

    [Fact]
    public void IsCompleted_CurrentGreaterThanTotal_ReturnsTrue()
    {
        Assert.True(ReadingProgressCalculator.IsCompleted(11, 10));
    }

    [Fact]
    public void IsCompleted_CurrentLessThanTotal_ReturnsFalse()
    {
        Assert.False(ReadingProgressCalculator.IsCompleted(5, 10));
    }

    [Fact]
    public void IsCompleted_ZeroTotalPages_ReturnsFalse()
    {
        Assert.False(ReadingProgressCalculator.IsCompleted(0, 0));
    }

    // ─── ApplyProgress ────────────────────────────────────────────────────────

    [Fact]
    public void ApplyProgress_UpdatesCurrentPageAndPercent()
    {
        var progress = new ReadingProgress { TotalPages = 10 };
        var now = DateTime.UtcNow;

        ReadingProgressCalculator.ApplyProgress(progress, 7, now);

        Assert.Equal(7, progress.CurrentPage);
        Assert.Equal(70.0, progress.PercentComplete);
        Assert.Equal(now, progress.LastReadAt);
    }

    [Fact]
    public void ApplyProgress_OnCompletion_SetsCompletedAt()
    {
        var progress = new ReadingProgress { TotalPages = 10 };
        var now = DateTime.UtcNow;

        ReadingProgressCalculator.ApplyProgress(progress, 10, now);

        Assert.NotNull(progress.CompletedAt);
        Assert.Equal(now, progress.CompletedAt);
    }

    [Fact]
    public void ApplyProgress_AlreadyCompleted_DoesNotOverwriteCompletedAt()
    {
        var original = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var progress = new ReadingProgress
        {
            TotalPages = 10,
            CompletedAt = original
        };

        ReadingProgressCalculator.ApplyProgress(progress, 10, DateTime.UtcNow);

        Assert.Equal(original, progress.CompletedAt);
    }

    [Fact]
    public void ApplyProgress_NotFinished_CompletedAtRemainsNull()
    {
        var progress = new ReadingProgress { TotalPages = 10 };

        ReadingProgressCalculator.ApplyProgress(progress, 5, DateTime.UtcNow);

        Assert.Null(progress.CompletedAt);
    }

    // ─── Mode persistence helpers ─────────────────────────────────────────────

    [Theory]
    [InlineData(ReaderMode.SinglePage)]
    [InlineData(ReaderMode.Longstrip)]
    [InlineData(ReaderMode.DoublePage)]
    [InlineData(ReaderMode.DoublePageManga)]
    [InlineData(ReaderMode.FitWidth)]
    [InlineData(ReaderMode.FitHeight)]
    public void ReaderMode_RoundTripViaInt_PreservesValue(ReaderMode mode)
    {
        var asInt = (int)mode;
        var restored = (ReaderMode)asInt;
        Assert.Equal(mode, restored);
    }

    // ─── Reading direction helpers ────────────────────────────────────────────

    [Theory]
    [InlineData(ReadingDirection.LeftToRight)]
    [InlineData(ReadingDirection.RightToLeft)]
    public void ReadingDirection_RoundTripViaInt_PreservesValue(ReadingDirection direction)
    {
        var asInt = (int)direction;
        var restored = (ReadingDirection)asInt;
        Assert.Equal(direction, restored);
    }
}
