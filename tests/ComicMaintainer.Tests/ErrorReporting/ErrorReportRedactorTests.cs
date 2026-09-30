using ComicMaintainer.Core.ErrorReporting;

namespace ComicMaintainer.Tests.ErrorReporting;

/// <summary>
/// Redaction is the only thing standing between a user's private library and a
/// public issue tracker, so each rule gets an explicit test.
/// </summary>
public class ErrorReportRedactorTests
{
    [Fact]
    public void Redact_ReplacesUnixDirectoriesButKeepsTheFilename()
    {
        var result = ErrorReportRedactor.Redact("Could not open /home/alice/comics/Saga/Saga 001.cbz");

        Assert.DoesNotContain("alice", result);
        Assert.DoesNotContain("comics", result);
        Assert.Contains("Saga 001.cbz", result);
        Assert.Contains(ErrorReportRedactor.PathMask, result);
    }

    [Fact]
    public void Redact_ReplacesWindowsDirectories()
    {
        var result = ErrorReportRedactor.Redact(@"Access denied: C:\Users\alice\Comics\issue.cbz");

        Assert.DoesNotContain("alice", result);
        Assert.Contains("issue.cbz", result);
    }

    [Fact]
    public void Redact_ReplacesDirectoriesThatContainSpaces()
    {
        // Stopping at the first space would leave "My Comics/Private Series"
        // — the whole library hierarchy — in a public issue.
        var result = ErrorReportRedactor.Redact("Could not open /home/alice/My Comics/Private Series/Issue 1.cbz");

        Assert.DoesNotContain("alice", result);
        Assert.DoesNotContain("My Comics", result);
        Assert.DoesNotContain("Private Series", result);
        Assert.Contains("Issue 1.cbz", result);
        Assert.Contains(ErrorReportRedactor.PathMask, result);
    }

    [Fact]
    public void Redact_ReplacesWindowsDirectoriesThatContainSpaces()
    {
        var result = ErrorReportRedactor.Redact(@"Access denied: C:\Users\alice\My Comics\Private Series\Issue 1.cbz");

        Assert.DoesNotContain("alice", result);
        Assert.DoesNotContain("My Comics", result);
        Assert.DoesNotContain("Private Series", result);
        Assert.Contains("Issue 1.cbz", result);
    }

    [Fact]
    public void Redact_MasksEmailAddresses()
    {
        var result = ErrorReportRedactor.Redact("Delivery to alice@example.com was rejected");

        Assert.DoesNotContain("alice@example.com", result);
        Assert.Contains(ErrorReportRedactor.Mask, result);
    }

    [Theory]
    [InlineData("SmtpPassword=hunter2sekrit")]
    [InlineData("ComicVineApiKey: abcdef0123456789")]
    [InlineData("\"token\": \"abcdef0123456789\"")]
    [InlineData("api_key=abcdef0123456789")]
    [InlineData("Authorization=abcdef0123456789")]
    public void Redact_MasksSecretAssignments(string input)
    {
        var result = ErrorReportRedactor.Redact(input);

        Assert.DoesNotContain("hunter2sekrit", result);
        Assert.DoesNotContain("abcdef0123456789", result);
        Assert.Contains(ErrorReportRedactor.Mask, result);
    }

    [Fact]
    public void Redact_MasksBearerTokens()
    {
        var result = ErrorReportRedactor.Redact("Request failed with header " + "Bearer" + " aVeryLongOpaqueTokenValue");

        Assert.DoesNotContain("aVeryLongOpaqueTokenValue", result);
    }

    [Fact]
    public void Redact_MasksGitHubTokenPrefixes()
    {
        var result = ErrorReportRedactor.Redact("token ghp_0123456789abcdefghijABCDEFGHIJ rejected");

        Assert.DoesNotContain("ghp_0123456789abcdefghijABCDEFGHIJ", result);
    }

    [Fact]
    public void Redact_MasksCredentialsEmbeddedInUrls()
    {
        var result = ErrorReportRedactor.Redact("connecting to smtp://" + "alice:hunter2" + "@mail.example.com");

        Assert.DoesNotContain("hunter2", result);
        Assert.DoesNotContain("alice", result);
        Assert.Contains("mail.example.com", result);
    }

    [Fact]
    public void Redact_MasksLiteralConfiguredSecretsWithNoKeyBesideThem()
    {
        // A third-party library echoing the credential it was handed, with no
        // surrounding key name for the pattern rules to latch onto.
        var result = ErrorReportRedactor.Redact(
            "server said: S3cretAppPassword is not accepted",
            new[] { "S3cretAppPassword" });

        Assert.DoesNotContain("S3cretAppPassword", result);
    }

    [Fact]
    public void Redact_IgnoresVeryShortLiteralSecretsToAvoidMaskingOrdinaryWords()
    {
        var result = ErrorReportRedactor.Redact("the file was not found", new[] { "the" });

        Assert.Contains("the file", result);
    }

    [Fact]
    public void Redact_TruncatesLongValues()
    {
        var result = ErrorReportRedactor.Redact(new string('x', 500), maxLength: 100);

        Assert.NotNull(result);
        Assert.True(result!.Length <= 100);
        Assert.EndsWith("[truncated]", result);
    }

    [Fact]
    public void Redact_PassesThroughNullAndEmpty()
    {
        Assert.Null(ErrorReportRedactor.Redact(null));
        Assert.Equal(string.Empty, ErrorReportRedactor.Redact(string.Empty));
    }

    [Fact]
    public void RedactStackTrace_KeepsLeadingFramesAndRedactsPaths()
    {
        var trace = string.Join('\n', Enumerable.Range(0, 60)
            .Select(i => $"   at Ns.Type.Method{i}() in /home/alice/src/File{i}.cs:line {i}"));

        var result = ErrorReportRedactor.RedactStackTrace(trace, Array.Empty<string>(), maxFrames: 10);

        Assert.NotNull(result);
        Assert.DoesNotContain("alice", result);
        Assert.Contains("Method0", result);
        Assert.Contains("additional frames omitted", result);
    }

    [Fact]
    public void RedactStackTrace_RespectsTheLengthCap()
    {
        var trace = string.Join('\n', Enumerable.Range(0, 60)
            .Select(i => $"   at Ns.Type.VeryLongMethodNameNumber{i}()"));

        var result = ErrorReportRedactor.RedactStackTrace(trace, Array.Empty<string>(), maxFrames: 40, maxLength: 200);

        Assert.NotNull(result);
        Assert.True(result!.Length <= 240);
    }

    [Fact]
    public void Truncate_LeavesShortValuesUntouched()
    {
        Assert.Equal("short", ErrorReportRedactor.Truncate("short", 100));
    }
}
