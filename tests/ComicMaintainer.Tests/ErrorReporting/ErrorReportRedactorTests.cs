using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting.Services;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.ErrorReporting;

/// <summary>
/// Redaction is the hard gate for this feature: anything it misses is
/// published to a public issue tracker by a user who believed the payload was
/// safe. These tests are therefore written as disclosure tests — each one
/// asserts that a specific sensitive value is <em>absent</em> from the output,
/// rather than that the output matches some expected shape.
/// </summary>
public class ErrorReportRedactorTests
{
    private static readonly ErrorReportRedactor Redactor = new();

    [Theory]
    // Absolute POSIX paths reveal the library layout and the user's account name.
    [InlineData("/home/alice/Comics/Batman/Batman 001.cbz", "alice")]
    [InlineData("/mnt/media/Manga/Berserk/Berserk v01.cbz", "Berserk")]
    // Windows paths, including the drive letter and the user profile.
    [InlineData(@"C:\Users\alice\Comics\Saga 12.cbr", "alice")]
    // UNC shares disclose an internal host name.
    [InlineData(@"\\NAS01\media\comics\Hellboy.cbz", "NAS01")]
    public void Redact_RemovesFilesystemPaths(string input, string secret)
    {
        var result = Redactor.Redact(input);

        Assert.DoesNotContain(secret, result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ErrorReportRedactor.Placeholder, result);
    }

    [Theory]
    [InlineData("Contact admin@example.com for help", "admin@example.com")]
    [InlineData("SMTP auth failed for user bob.smith@mail.example.org", "bob.smith@mail.example.org")]
    public void Redact_RemovesEmailAddresses(string input, string secret)
    {
        Assert.DoesNotContain(secret, Redactor.Redact(input), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Token-shaped values are assembled at run time from fragments so that
    /// this file never contains a literal that a secret scanner — ours or
    /// GitHub's — would have to decide about.
    /// </summary>
    public static TheoryData<string, string> TokenCases()
    {
        var ghp = "gh" + "p_" + new string('A', 36);
        var pat = "github" + "_pat_" + new string('B', 22) + "_" + new string('c', 59);
        var jwt = "ey" + "JhbGciOiJIUzI1NiJ9."
                  + "eyJzdWIiOiJhbGljZSIsInJvbGUiOiJBZG1pbiJ9."
                  + new string('d', 43);

        return new TheoryData<string, string>
        {
            { $"Request failed with token {ghp}", ghp },
            { $"Using {pat} for the API", pat },
            { $"Authorization header was {jwt}", jwt },
        };
    }

    [Theory]
    [MemberData(nameof(TokenCases))]
    public void Redact_RemovesTokens(string input, string secret)
    {
        Assert.DoesNotContain(secret, Redactor.Redact(input), StringComparison.Ordinal);
    }

    public static TheoryData<string, string> HeaderCases()
    {
        var bearer = "Bea" + "rer abc123def456ghi789";

        return new TheoryData<string, string>
        {
            { $"Authorization: {bearer}", "abc123def456ghi789" },
            { "Cookie: session=9f8e7d6c5b4a3210", "9f8e7d6c5b4a3210" },
            { "Remote-User: alice", "alice" },
            { "Remote-Groups: admins,readers", "admins" },
            { "X-Api-Key: 0123456789abcdef", "0123456789abcdef" },
        };
    }

    [Theory]
    [MemberData(nameof(HeaderCases))]
    public void Redact_RemovesAuthenticationHeaders(string input, string secret)
    {
        Assert.DoesNotContain(secret, Redactor.Redact(input), StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string, string> KeyValueCases() => new()
    {
        { "pass" + "word=hunter2000", "hunter2000" },
        { "api_key: 8badf00dcafebabe", "8badf00dcafebabe" },
        { "ConnectionString=\"Data Source=/Config/comics.db\"", "comics.db" },
        // Quoted values: stopping at the opening quote would leave the
        // credential in the text. Both quote styles, with values chosen so that
        // no other rule (token shape, path, e-mail) would catch them.
        { "to" + "ken=\"zqxjklmnbvfghdsa\"", "zqxjklmnbvfghdsa" },
        { "to" + "ken='qzwxecrvtbynumip'", "qzwxecrvtbynumip" },
        { "client_secret: \"plough rhythm crypt\"", "plough rhythm crypt" },
    };

    [Theory]
    [MemberData(nameof(KeyValueCases))]
    public void Redact_RemovesKeyValueSecrets(string input, string secret)
    {
        Assert.DoesNotContain(secret, Redactor.Redact(input), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Request from 192.168.1.42 failed", "192.168.1.42")]
    [InlineData("Peer fe80::1ff:fe23:4567:890a disconnected", "fe80::1ff:fe23:4567:890a")]
    public void Redact_RemovesIpAddresses(string input, string secret)
    {
        Assert.DoesNotContain(secret, Redactor.Redact(input), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redact_RemovesUrlPathAndQuery_ButKeepsHost()
    {
        var result = Redactor.Redact(
            "GET https://comicvine.gamespot.com/api/search/?api_key=secret123&query=Batman failed");

        Assert.DoesNotContain("secret123", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Batman", result, StringComparison.Ordinal);

        // The host is the diagnostically useful part — it identifies which
        // external provider failed — and is not user data.
        Assert.Contains("comicvine.gamespot.com", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_RemovesComicFileNames()
    {
        var result = Redactor.Redact("Failed to open Amazing Spider-Man 042 (2023).cbz");

        Assert.DoesNotContain("Spider-Man", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Amazing", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redact_KeepsSourceFileNamesInStackFrames()
    {
        // Source file names come from this repository, are already public, and
        // are what makes a stack trace actionable. Their *paths* still go.
        var result = Redactor.Redact(
            "   at ComicMaintainer.Core.Services.SeriesLibraryService.Scan() "
            + "in /src/ComicMaintainer.Core/Services/SeriesLibraryService.cs:line 1247");

        Assert.Contains("SeriesLibraryService.cs", result, StringComparison.Ordinal);
        Assert.Contains("1247", result, StringComparison.Ordinal);
        Assert.DoesNotContain("/src/ComicMaintainer.Core/Services/", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_PreservesExceptionTypeAndFrameMembers()
    {
        // If redaction destroyed these the report would be unactionable, which
        // is just as much a failure as leaking a path.
        var result = Redactor.Redact(
            "System.NullReferenceException: Object reference not set to an instance of an object.\n"
            + "   at ComicMaintainer.Core.Services.MetadataService.Apply()");

        Assert.Contains("System.NullReferenceException", result, StringComparison.Ordinal);
        Assert.Contains("ComicMaintainer.Core.Services.MetadataService.Apply", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_DropsDataFileNamesOutsideTheSourceTree()
    {
        // A `.json`, `.js` or `.cs` basename is not evidence of a repository
        // source file. Only a path that also names this application's source
        // tree may keep its file name; anything else is user data.
        var result = Redactor.Redact(
            "Could not read /home/alice/private-library.json or /srv/alice-notes.cs");

        Assert.DoesNotContain("private-library", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // Comic paths routinely contain spaces, and the path pattern stops at
    // whitespace; every fragment must still be redacted rather than the tail
    // surviving as plain text.
    [InlineData("/home/alice/My Comics/Batman Year One 001.cbz", "alice")]
    [InlineData("/home/alice/My Comics/Batman Year One 001.cbz", "Batman")]
    [InlineData(@"C:\Users\alice\My Documents\Saga Volume 3.cbr", "Saga")]
    public void Redact_RemovesPathsContainingSpaces(string input, string secret)
    {
        Assert.DoesNotContain(secret, Redactor.Redact(input), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redact_RemovesConfiguredSecretValues()
    {
        // Values configured in AppSettings (SMTP password, ComicVine key, the
        // GitHub token, watched directories) are scrubbed literally, because
        // no pattern can recognise an arbitrary user-chosen secret.
        var result = ErrorReportRedactor.RedactText(
            "Login failed using super-secret-smtp-password and comicvine-api-key-value",
            ["super-secret-smtp-password", "comicvine-api-key-value"]);

        Assert.DoesNotContain("super-secret-smtp-password", result, StringComparison.Ordinal);
        Assert.DoesNotContain("comicvine-api-key-value", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_RemovesShortConfiguredCredentials()
    {
        // A short password is a weak password, not a public one. Skipping it
        // because of its length would publish it verbatim whenever an exception
        // message quoted it back and no shape-based rule matched.
        var settings = new AppSettings
        {
            SmtpPassword = "pw42",
            ComicVineApiKey = "k9",
        };

        var monitor = new Mock<IOptionsMonitor<AppSettings>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(settings);

        var result = new ErrorReportRedactor(monitor.Object)
            .Redact("SMTP rejected credentials pw42 and provider key k9");

        Assert.DoesNotContain("pw42", result, StringComparison.Ordinal);
        Assert.DoesNotContain("k9", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_DoesNotLeakSecretsThroughOverlappingMatches()
    {
        // A path inside a URL inside a header: the single-pass design must
        // classify each character once rather than re-parsing its own output,
        // which is where multi-pass redactors typically leak.
        var credential = "Bea" + "rer " + new string('t', 30);
        var userInfo = "admin:" + "hunter2000";
        var input = $"Authorization: {credential} "
                    + $"while fetching https://{userInfo}@host.example.com/home/alice/Comics/x.cbz";

        var result = Redactor.Redact(input);

        Assert.DoesNotContain("alice", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2000", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(new string('t', 30), result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Redact_HandlesEmptyInput(string? input)
    {
        Assert.True(string.IsNullOrWhiteSpace(Redactor.Redact(input)));
    }

    [Fact]
    public void RedactLines_RedactsEveryLine()
    {
        var lines = new[]
        {
            "/home/alice/a.cbz",
            "admin@example.com",
            "clean line",
        };

        var result = Redactor.RedactLines(lines).ToList();

        Assert.Equal(3, result.Count);
        Assert.DoesNotContain("alice", result[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin@example.com", result[1], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("clean line", result[2]);
    }

    /// <summary>
    /// Random input must never throw and never take unbounded time: a hostile
    /// filename reaching the redactor should not be able to take the process
    /// down or hang the reporting path.
    /// </summary>
    [Fact]
    public void Redact_SurvivesRandomInput()
    {
        var random = new Random(20260929);
        const string alphabet = "abcXYZ019/\\:@.-_ \t\"'<>|?*$&=+%#[]{}()";

        for (var i = 0; i < 2000; i++)
        {
            var length = random.Next(0, 300);
            var value = string.Create(length, random, (span, rng) =>
            {
                for (var j = 0; j < span.Length; j++)
                {
                    span[j] = alphabet[rng.Next(alphabet.Length)];
                }
            });

            var result = Redactor.Redact(value);
            Assert.NotNull(result);
        }
    }

    /// <summary>
    /// Long runs of path- and token-like characters are the classic shape for
    /// catastrophic backtracking. The redactor must stay linear.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("a@a.")]
    [InlineData("Authorization:")]
    [InlineData("https://")]
    public void Redact_StaysLinearOnAdversarialInput(string prefix)
    {
        // 50k characters of the most backtracking-prone shape for each branch.
        var value = prefix + new string('a', 50_000);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = Redactor.Redact(value);
        started.Stop();

        Assert.NotNull(result);
        Assert.True(
            started.ElapsedMilliseconds < 2000,
            $"Redaction took {started.ElapsedMilliseconds} ms, which suggests backtracking.");
    }
}
