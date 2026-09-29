using ComicMaintainer.Core.ErrorReporting.Services;

namespace ComicMaintainer.Tests.ErrorReporting;

/// <summary>
/// Suppression is what keeps the issue tracker credible. Every rule here exists
/// because the matching failure is environmental — a full disk, a locked
/// database, a user closing a tab — and filing it as a code defect wastes a
/// maintainer's (or an agent's) time.
/// </summary>
public class ErrorReportSuppressionRulesTests
{
    [Theory]
    [InlineData("System.OperationCanceledException")]
    [InlineData("System.Threading.Tasks.TaskCanceledException")]
    [InlineData("Microsoft.AspNetCore.Connections.ConnectionResetException")]
    [InlineData("Microsoft.AspNetCore.Http.BadHttpRequestException")]
    [InlineData("System.Net.Http.HttpRequestException")]
    [InlineData("System.Net.Sockets.SocketException")]
    [InlineData("System.TimeoutException")]
    [InlineData("System.UnauthorizedAccessException")]
    [InlineData("System.IO.DriveNotFoundException")]
    [InlineData("System.IO.PathTooLongException")]
    public void ShouldSuppress_SuppressesEnvironmentalExceptionTypes(string type)
    {
        Assert.True(ErrorReportSuppressionRules.ShouldSuppress(type, "anything"));
    }

    [Theory]
    [InlineData("database is locked")]
    [InlineData("SQLite Error 5: 'database is busy'")]
    [InlineData("There is not enough space on the disk")]
    [InlineData("Disk quota exceeded")]
    [InlineData("Broken pipe")]
    [InlineData("The client disconnected")]
    [InlineData("The response has already started")]
    [InlineData("Read-only file system")]
    [InlineData("Permission denied")]
    [InlineData("Access to the path is denied")]
    public void ShouldSuppress_SuppressesEnvironmentalMessages(string message)
    {
        Assert.True(ErrorReportSuppressionRules.ShouldSuppress("System.Exception", message));
    }

    [Theory]
    [InlineData("System.NullReferenceException", "Object reference not set to an instance of an object.")]
    [InlineData("System.InvalidOperationException", "Sequence contains no elements")]
    [InlineData("System.IndexOutOfRangeException", "Index was outside the bounds of the array.")]
    [InlineData("System.ArgumentNullException", "Value cannot be null. (Parameter 'series')")]
    public void ShouldSuppress_AllowsGenuineDefects(string type, string message)
    {
        Assert.False(ErrorReportSuppressionRules.ShouldSuppress(type, message));
    }

    [Fact]
    public void ShouldSuppress_UnwrapsInnerExceptions()
    {
        // ASP.NET and EF routinely wrap a cancellation several layers deep;
        // checking only the outermost type would let all of them through.
        var wrapped = new InvalidOperationException(
            "A task failed",
            new InvalidOperationException("inner", new TaskCanceledException()));

        Assert.True(ErrorReportSuppressionRules.ShouldSuppress(wrapped));
    }

    [Fact]
    public void ShouldSuppress_UnwrapsAggregateExceptions()
    {
        var aggregate = new AggregateException(
            new InvalidOperationException("unrelated"),
            new OperationCanceledException());

        Assert.True(ErrorReportSuppressionRules.ShouldSuppress(aggregate));
    }

    [Fact]
    public void ShouldSuppress_AllowsGenuineDefectWrappedInAggregate()
    {
        var aggregate = new AggregateException(new NullReferenceException("boom"));

        Assert.False(ErrorReportSuppressionRules.ShouldSuppress(aggregate));
    }

    [Fact]
    public void ShouldSuppress_HandlesNullInput()
    {
        Assert.False(ErrorReportSuppressionRules.ShouldSuppress((Exception?)null));
        Assert.False(ErrorReportSuppressionRules.ShouldSuppress(null, null));
    }

    [Fact]
    public void ShouldSuppress_IsCaseInsensitive()
    {
        Assert.True(ErrorReportSuppressionRules.ShouldSuppress("System.Exception", "DATABASE IS LOCKED"));
    }
}
