using ComicMaintainer.Core.ErrorReporting.Services;

namespace ComicMaintainer.Tests.ErrorReporting;

/// <summary>
/// The fingerprint decides whether this feature is useful or a spam cannon: it
/// must be stable enough that the same defect collapses into one issue, and
/// discriminating enough that two different defects do not.
/// </summary>
public class ErrorFingerprinterTests
{
    private const string Trace = """
        System.NullReferenceException: Object reference not set to an instance of an object.
           at ComicMaintainer.Core.Services.SeriesLibraryService.Scan(String path)
           at ComicMaintainer.Core.Services.SeriesLibraryService.Refresh()
           at ComicMaintainer.WebApi.Controllers.FilesController.Get()
        """;

    [Fact]
    public void Compute_IsDeterministic()
    {
        var first = ErrorFingerprinter.Compute("System.NullReferenceException", "boom", Trace, "2.0.310");
        var second = ErrorFingerprinter.Compute("System.NullReferenceException", "boom", Trace, "2.0.310");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_ReturnsShortStableHexIdentifier()
    {
        var fingerprint = ErrorFingerprinter.Compute("System.Exception", "x", Trace, "2.0.310");

        Assert.Equal(16, fingerprint.Length);
        Assert.Matches("^[0-9a-f]{16}$", fingerprint);
    }

    [Theory]
    // Numbers, identifiers and quoted values vary between occurrences of the
    // same defect. If they contributed to the hash, every occurrence would file
    // its own issue.
    [InlineData("Timed out after 30 ms", "Timed out after 5000 ms")]
    [InlineData("Job 4e2a is stuck", "Job 91fc is stuck")]
    [InlineData("Could not load \"alpha\"", "Could not load \"beta\"")]
    [InlineData(
        "Missing 550e8400-e29b-41d4-a716-446655440000",
        "Missing 7c9e6679-7425-40de-944b-e07fc1f90ae7")]
    public void Compute_IgnoresVaryingDetailInMessages(string first, string second)
    {
        Assert.Equal(
            ErrorFingerprinter.Compute("System.Exception", first, Trace, "2.0.310"),
            ErrorFingerprinter.Compute("System.Exception", second, Trace, "2.0.310"));
    }

    [Fact]
    public void Compute_IgnoresRedactionPlaceholders()
    {
        // Two instances with different library layouts produce different
        // redaction outcomes for the same defect; they must still collapse.
        Assert.Equal(
            ErrorFingerprinter.Compute("System.IO.IOException", "Cannot open [redacted].cbz", Trace, "2.0.310"),
            ErrorFingerprinter.Compute("System.IO.IOException", "Cannot open [redacted]", Trace, "2.0.310"));
    }

    [Fact]
    public void Compute_DistinguishesExceptionTypes()
    {
        Assert.NotEqual(
            ErrorFingerprinter.Compute("System.NullReferenceException", "boom", Trace, "2.0.310"),
            ErrorFingerprinter.Compute("System.InvalidOperationException", "boom", Trace, "2.0.310"));
    }

    [Fact]
    public void Compute_DistinguishesCallSites()
    {
        var other = Trace.Replace("SeriesLibraryService.Scan", "MetadataService.Apply", StringComparison.Ordinal);

        Assert.NotEqual(
            ErrorFingerprinter.Compute("System.NullReferenceException", "boom", Trace, "2.0.310"),
            ErrorFingerprinter.Compute("System.NullReferenceException", "boom", other, "2.0.310"));
    }

    [Fact]
    public void Compute_DistinguishesAppVersions()
    {
        // Deliberate: the same trace in a later release is a regression worth
        // hearing about, not a duplicate of an issue that was already closed.
        Assert.NotEqual(
            ErrorFingerprinter.Compute("System.Exception", "boom", Trace, "2.0.310"),
            ErrorFingerprinter.Compute("System.Exception", "boom", Trace, "2.1.0"));
    }

    [Fact]
    public void ExtractSignificantFrames_PrefersApplicationFrames()
    {
        var mixed = """
               at System.IO.FileStream.ReadCore()
               at Microsoft.EntityFrameworkCore.Query.Execute()
               at ComicMaintainer.Core.Services.SeriesLibraryService.Scan()
            """;

        var frames = ErrorFingerprinter.ExtractSignificantFrames(mixed);

        Assert.Contains(frames, f => f.Contains("SeriesLibraryService.Scan", StringComparison.Ordinal));
        Assert.DoesNotContain(frames, f => f.StartsWith("System.IO", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractSignificantFrames_FallsBackWhenNoApplicationFrames()
    {
        // A failure entirely inside a framework is still worth fingerprinting;
        // returning nothing would collapse every such error into one bucket.
        var frames = ErrorFingerprinter.ExtractSignificantFrames(
            "   at System.IO.FileStream.ReadCore()\n   at System.Threading.Tasks.Task.Wait()");

        Assert.NotEmpty(frames);
    }

    [Theory]
    [InlineData("ComicMaintainer.Core.Reader.Services.PageService.Render", "reader")]
    [InlineData("ComicMaintainer.Core.Services.ComicVineSeriesMetadataService.Search", "metadata")]
    [InlineData("ComicMaintainer.Core.Services.ComicEmailService.Send", "email")]
    [InlineData("ComicMaintainer.Core.Services.FileWatcherService.OnChanged", "watcher")]
    [InlineData("ComicMaintainer.WebApi.Controllers.AuthController.Login", "auth")]
    [InlineData("ComicMaintainer.Core.Services.SeriesLibraryService.Scan", "core")]
    public void DeriveArea_MapsTopFrameToLabel(string frame, string expected)
    {
        Assert.Equal(expected, ErrorFingerprinter.DeriveArea($"   at {frame}()"));
    }

    [Fact]
    public void Compute_HandlesMissingStackTrace()
    {
        var fingerprint = ErrorFingerprinter.Compute("System.Exception", "boom", null, "2.0.310");

        Assert.Equal(16, fingerprint.Length);
    }

    /// <summary>
    /// Distinct inputs should not collide. A collision would silently merge two
    /// unrelated defects into one issue, and the second would never be fixed.
    /// </summary>
    [Fact]
    public void Compute_DoesNotCollideAcrossManyDistinctInputs()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 5000; i++)
        {
            var trace = $"   at ComicMaintainer.Core.Services.Service{i}.Method{i}()";
            Assert.True(
                seen.Add(ErrorFingerprinter.Compute($"System.Exception{i}", "boom", trace, "2.0.310")),
                $"Collision at iteration {i}.");
        }
    }
}
