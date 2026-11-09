using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.WebApi.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// The OPDS catalog through the real pipeline: HTTP Basic with a password or an API key, the feeds a client walks, the
/// page stream and its progress, the download, and the base path.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OpdsCatalogIntegrationTests : IClassFixture<OpdsHostFixture>
{
    private const string Catalog = "/opds/v1.2/catalog";
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Pse = "http://vaemendis.net/opds-pse/ns";
    private const string StreamRel = "http://vaemendis.net/opds-pse/stream";
    private const string AcquisitionRel = "http://opds-spec.org/acquisition/open-access";
    private const string ThumbnailRel = "http://opds-spec.org/image/thumbnail";

    private readonly OpdsTestHost _host;

    public OpdsCatalogIntegrationTests(OpdsHostFixture fixture)
    {
        _host = fixture.Host;
    }

    private static void AssertFeedKind(HttpResponseMessage response, string kind)
    {
        var contentType = response.Content.Headers.ContentType!;
        Assert.Equal("application/atom+xml", contentType.MediaType);
        Assert.Contains(contentType.Parameters, p => p.Name == "profile" && p.Value == "opds-catalog");
        Assert.Contains(contentType.Parameters, p => p.Name == "kind" && p.Value == kind);
    }

    private static string UniqueSeries() => "Series " + Guid.NewGuid().ToString("N")[..10];

    private static string? Href(XElement parent, string rel) =>
        parent.Elements(Atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") == rel)?.Attribute("href")?.Value;

    private static IReadOnlyList<XElement> Entries(XDocument feed) => feed.Root!.Elements(Atom + "entry").ToList();

    private static XElement EntryTitled(XDocument feed, string title) =>
        Entries(feed).Single(e => (string?)e.Element(Atom + "title") == title);

    private async Task<(ApplicationUser User, string SeriesTitle, string Comic, XElement SeriesEntry)> UserWithComicAsync(params (string Name, string Content)[] pages)
    {
        var user = await _host.CreateUserAsync();
        var series = UniqueSeries();
        var comic = await _host.AddComicAsync(Path.Join(series, series + " - Chapter 1.cbz"), pages);
        var entry = EntryTitled(await SearchFeedAsync(user, series), series);
        return (user, series, comic, entry);
    }

    private async Task<XDocument> SearchFeedAsync(ApplicationUser user, string term)
    {
        using var response = await _host.GetAsync($"/opds/v1.2/search?q={Uri.EscapeDataString(term)}", user.UserName, OpdsTestHost.Password);
        return await OpdsTestHost.ReadFeedAsync(response);
    }

    [Fact]
    public async Task AClientWalksTheCatalogWithAPassword()
    {
        var (user, series, comic, seriesEntry) = await UserWithComicAsync(("p1.jpg", "first page"), ("p2.jpg", "second page"), ("p3.jpg", "third page"));

        using var catalogResponse = await _host.GetAsync(Catalog, user.UserName, OpdsTestHost.Password);
        var catalog = await OpdsTestHost.ReadFeedAsync(catalogResponse);

        AssertFeedKind(catalogResponse, "navigation");
        Assert.Equal(
            new[] { "Continue Reading", "Series Updates", "Newly Added", "All Series" },
            Entries(catalog).Select(e => (string?)e.Element(Atom + "title")));

        // The series entry leads to the issues, which carry the acquisition and stream links.
        var seriesHref = Href(seriesEntry, "subsection")!;
        using var issuesResponse = await _host.GetAsync(seriesHref, user.UserName, OpdsTestHost.Password);
        var issues = await OpdsTestHost.ReadFeedAsync(issuesResponse);
        AssertFeedKind(issuesResponse, "acquisition");
        var issue = Assert.Single(Entries(issues));

        var acquisition = issue.Elements(Atom + "link").Single(l => (string?)l.Attribute("rel") == AcquisitionRel);
        Assert.Equal("application/vnd.comicbook+zip", acquisition.Attribute("type")!.Value);

        var stream = issue.Elements(Atom + "link").Single(l => (string?)l.Attribute("rel") == StreamRel);
        Assert.Equal("3", stream.Attribute(Pse + "count")!.Value);
        Assert.Equal("image/jpeg", stream.Attribute("type")!.Value);
        Assert.Null(stream.Attribute(Pse + "lastRead"));
        Assert.Contains("{pageNumber}", stream.Attribute("href")!.Value);

        using var page = await _host.GetAsync(stream.Attribute("href")!.Value.Replace("{pageNumber}", "1"), user.UserName, OpdsTestHost.Password);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("second page", await page.Content.ReadAsStringAsync());
        Assert.Equal("image/jpeg", page.Content.Headers.ContentType!.MediaType);

        using var download = await _host.GetAsync(acquisition.Attribute("href")!.Value, user.UserName, OpdsTestHost.Password);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(await File.ReadAllBytesAsync(comic), await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/vnd.comicbook+zip", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("bytes", download.Headers.AcceptRanges.Single());
    }

    [Theory]
    [InlineData("/opds/v1.2")]
    [InlineData("/opds/v1.2/")]
    public async Task TheCatalogIsAlsoAtItsBareAddress(string address)
    {
        var user = await _host.CreateUserAsync();

        using var response = await _host.GetAsync(address, user.UserName, OpdsTestHost.Password);

        var feed = await OpdsTestHost.ReadFeedAsync(response);
        Assert.Equal(4, Entries(feed).Count);
    }

    [Fact]
    public async Task AnApiKeyWorksAsThePasswordForItsOwnerOnly()
    {
        var owner = await _host.CreateUserAsync();
        var other = await _host.CreateUserAsync();
        var key = await _host.CreateApiKeyAsync(owner.Id);

        using var valid = await _host.GetAsync(Catalog, owner.UserName, key);
        using var wrongUser = await _host.GetAsync(Catalog, other.UserName, key);

        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongUser.StatusCode);
    }

    [Fact]
    public async Task UsingAKeyIsRecordedAndKeepsWorkingUntilItIsRevoked()
    {
        var user = await _host.CreateUserAsync();
        var key = await _host.CreateApiKeyAsync(user.Id);

        using (var first = await _host.GetAsync(Catalog, user.UserName, key))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var scope = _host.Services.CreateScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(user.Id);
            Assert.NotNull(saved!.ApiKeyLastUsedAt);
        }

        using (var cached = await _host.GetAsync(Catalog, user.UserName, key))
        {
            Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        }

        using (var scope = _host.Services.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IAuthService>().RevokeApiKeyAsync(user.Id));
        }

        using var revoked = await _host.GetAsync(Catalog, user.UserName, key);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Theory]
    [InlineData("ReadOnly")]
    [InlineData("User")]
    [InlineData("Admin")]
    public async Task EveryRoleCanRead(string role)
    {
        var user = await _host.CreateUserAsync(role);

        using var response = await _host.GetAsync(Catalog, user.UserName, OpdsTestHost.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WithoutCredentialsTheCatalogAsksForBasic()
    {
        using var response = await _host.GetAsync(Catalog);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(OpdsBasicAuthenticationHandler.Challenge, response.Headers.WwwAuthenticate.ToString());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task TheRestOfTheAppNeverAsksForBasicOrAcceptsIt()
    {
        var user = await _host.CreateUserAsync();

        using var anonymous = await _host.GetAsync("/api/auth/user");
        using var withBasic = await _host.GetAsync("/api/auth/user", user.UserName, OpdsTestHost.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withBasic.StatusCode);
        Assert.DoesNotContain(anonymous.Headers.WwwAuthenticate, h => h.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(withBasic.Headers.WwwAuthenticate, h => h.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Bearer abc")]
    [InlineData("Basic !!!not-base64!!!")]
    [InlineData("Basic bm9jb2xvbg==")] // "nocolon"
    [InlineData("Basic OnBhc3N3b3Jk")] // ":password" has no username
    [InlineData("Basic dXNlcjo=")] // "user:" has no password
    [InlineData("Basic /////w==")] // not UTF-8
    public async Task MalformedCredentialsAreChallenged(string authorization)
    {
        using var response = await _host.GetAsync(Catalog, configure: r => r.Headers.TryAddWithoutValidation("Authorization", authorization));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(OpdsBasicAuthenticationHandler.Challenge, response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task AnOverlongSecretIsRefusedWithoutBeingChecked()
    {
        var user = await _host.CreateUserAsync();

        using var response = await _host.GetAsync(Catalog, user.UserName, new string('x', 1025));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AWrongPasswordIsRefusedAndRepeatedOnesLockTheAccount()
    {
        var user = await _host.CreateUserAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await _host.GetAsync(Catalog, user.UserName, "Wr0ngPassword");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var locked = await _host.GetAsync(Catalog, user.UserName, OpdsTestHost.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
    }

    [Fact]
    public async Task ADisabledAccountIsRefusedEvenWhileItsPasswordIsRemembered()
    {
        var user = await _host.CreateUserAsync();
        using (var first = await _host.GetAsync(Catalog, user.UserName, OpdsTestHost.Password))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        await _host.ChangeUserAsync(user.Id, async (users, account) =>
        {
            account.IsActive = false;
            await users.UpdateAsync(account);
        });

        using var refused = await _host.GetAsync(Catalog, user.UserName, OpdsTestHost.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task ChangingThePasswordEndsTheOldOne()
    {
        var user = await _host.CreateUserAsync();
        using (var first = await _host.GetAsync(Catalog, user.UserName, OpdsTestHost.Password))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        await _host.ChangeUserAsync(user.Id, async (users, account) =>
            Assert.True((await users.ChangePasswordAsync(account, OpdsTestHost.Password, "N3wPassw0rdOk")).Succeeded));

        using var oldPassword = await _host.GetAsync(Catalog, user.UserName, OpdsTestHost.Password);
        using var newPassword = await _host.GetAsync(Catalog, user.UserName, "N3wPassw0rdOk");

        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);
    }

    [Fact]
    public async Task ALockedOutAccountIsRefusedEvenWhileItsPasswordIsRemembered()
    {
        var user = await _host.CreateUserAsync();
        using (var first = await _host.GetAsync(Catalog, user.UserName, OpdsTestHost.Password))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        await _host.ChangeUserAsync(user.Id, (users, account) => users.SetLockoutEndDateAsync(account, DateTimeOffset.UtcNow.AddHours(1)));

        using var refused = await _host.GetAsync(Catalog, user.UserName, OpdsTestHost.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task TooManyAttemptsFromOneClientAreRefusedAndCountAgainstTheSignInPage()
    {
        var user = await _host.CreateUserAsync();
        const string client = "198.51.100.77";

        for (var attempt = 1; attempt <= AuthenticationRateLimit.PermitLimit; attempt++)
        {
            using var wrong = await _host.GetAsync(Catalog, "nobody", "guess" + attempt, client: client);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var limited = await _host.GetAsync(Catalog, user.UserName, OpdsTestHost.Password, client: client);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.False(limited.Headers.Contains("WWW-Authenticate"), "A rate-limited client is told to wait, not to send credentials");
        var seconds = limited.Headers.RetryAfter?.Delta?.TotalSeconds;
        Assert.NotNull(seconds);
        Assert.InRange(seconds.Value, 1, AuthenticationRateLimit.Window.TotalSeconds);
        var body = await limited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("Too many requests", body.GetProperty("error").GetString());

        using var login = await _host.GetAsync("/api/auth/login", client: client, method: HttpMethod.Post, configure: r => r.Content = JsonContent.Create(new { username = "x", password = "y" }));
        Assert.Equal(HttpStatusCode.TooManyRequests, login.StatusCode);
    }

    [Fact]
    public async Task ARememberedCredentialIsNotHeldBackByTheLimit()
    {
        var user = await _host.CreateUserAsync();
        const string client = "198.51.100.78";

        using (var signIn = await _host.GetAsync(Catalog, user.UserName, OpdsTestHost.Password, client: client))
        {
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        }

        for (var attempt = 1; attempt < AuthenticationRateLimit.PermitLimit; attempt++)
        {
            using var wrong = await _host.GetAsync(Catalog, "nobody", "guess" + attempt, client: client);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var stillSignedIn = await _host.GetAsync(Catalog, user.UserName, OpdsTestHost.Password, client: client);
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
    }

    [Fact]
    public async Task ThePageStreamStartsAtZeroAndRecordsReaderPages()
    {
        var (user, _, comic, _) = await UserWithComicAsync(("p1.jpg", "first"), ("p2.jpg", "second"), ("p3.jpg", "third"));
        var id = await _host.FileIdAsync(comic);

        using (var first = await _host.GetAsync($"/opds/v1.2/issues/{id}/pages/0", user.UserName, OpdsTestHost.Password))
        {
            Assert.Equal("first", await first.Content.ReadAsStringAsync());
        }

        var progress = await _host.ProgressAsync(user.Id, comic);
        Assert.Equal(1, progress!.CurrentPage);
        Assert.Equal(3, progress.TotalPages);
        Assert.Null(progress.CompletedAt);

        using (var last = await _host.GetAsync($"/opds/v1.2/issues/{id}/pages/2", user.UserName, OpdsTestHost.Password))
        {
            Assert.Equal("third", await last.Content.ReadAsStringAsync());
        }

        progress = await _host.ProgressAsync(user.Id, comic);
        Assert.Equal(3, progress!.CurrentPage);
        Assert.NotNull(progress.CompletedAt);

        using var pastTheEnd = await _host.GetAsync($"/opds/v1.2/issues/{id}/pages/3", user.UserName, OpdsTestHost.Password);
        using var negative = await _host.GetAsync($"/opds/v1.2/issues/{id}/pages/-1", user.UserName, OpdsTestHost.Password);

        Assert.Equal(HttpStatusCode.NotFound, pastTheEnd.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Equal(3, (await _host.ProgressAsync(user.Id, comic))!.CurrentPage);
    }

    [Fact]
    public async Task TheFeedReportsProgressPerUserAndNumberedFromOne()
    {
        var (user, series, comic, seriesEntry) = await UserWithComicAsync(("p1.jpg", "first"), ("p2.jpg", "second"), ("p3.jpg", "third"));
        var other = await _host.CreateUserAsync();
        var id = await _host.FileIdAsync(comic);
        using (await _host.GetAsync($"/opds/v1.2/issues/{id}/pages/1", user.UserName, OpdsTestHost.Password))
        {
        }

        var issuesHref = Href(seriesEntry, "subsection")!;
        using var forReader = await _host.GetAsync(issuesHref, user.UserName, OpdsTestHost.Password);
        using var forOther = await _host.GetAsync(issuesHref, other.UserName, OpdsTestHost.Password);

        var readerStream = Assert.Single(Entries(await OpdsTestHost.ReadFeedAsync(forReader))).Elements(Atom + "link").Single(l => (string?)l.Attribute("rel") == StreamRel);
        var otherStream = Assert.Single(Entries(await OpdsTestHost.ReadFeedAsync(forOther))).Elements(Atom + "link").Single(l => (string?)l.Attribute("rel") == StreamRel);

        // Page number 1 of the stream is page 2 of the comic, and pse:lastRead counts from 1.
        Assert.Equal("2", readerStream.Attribute(Pse + "lastRead")!.Value);
        Assert.EndsWith("Z", readerStream.Attribute(Pse + "lastReadDate")!.Value, StringComparison.Ordinal);
        Assert.Null(otherStream.Attribute(Pse + "lastRead"));
    }

    [Fact]
    public async Task ContinueReadingListsTheIssueInProgress()
    {
        var (user, _, comic, _) = await UserWithComicAsync(("p1.jpg", "first"), ("p2.jpg", "second"), ("p3.jpg", "third"));
        var id = await _host.FileIdAsync(comic);
        using (await _host.GetAsync($"/opds/v1.2/issues/{id}/pages/0", user.UserName, OpdsTestHost.Password))
        {
        }

        using var response = await _host.GetAsync("/opds/v1.2/continue-reading", user.UserName, OpdsTestHost.Password);

        var feed = await OpdsTestHost.ReadFeedAsync(response);
        var entry = Assert.Single(Entries(feed));
        Assert.Contains($"/issues/{id}/", Href(entry, AcquisitionRel));
    }

    [Fact]
    public async Task ContinueReadingIsEmptyBeforeAnyReading()
    {
        var user = await _host.CreateUserAsync();

        using var response = await _host.GetAsync("/opds/v1.2/continue-reading", user.UserName, OpdsTestHost.Password);

        Assert.Empty(Entries(await OpdsTestHost.ReadFeedAsync(response)));
    }

    [Fact]
    public async Task SeriesUpdatesAndNewlyAddedAreNavigationFeeds()
    {
        var (user, series, _, _) = await UserWithComicAsync(("p1.jpg", "first"));

        using var updates = await _host.GetAsync("/opds/v1.2/series-updates", user.UserName, OpdsTestHost.Password);
        using var added = await _host.GetAsync("/opds/v1.2/newly-added", user.UserName, OpdsTestHost.Password);

        AssertFeedKind(updates, "navigation");
        Assert.NotNull(EntryTitled(await OpdsTestHost.ReadFeedAsync(added), series));
        Assert.Equal(Atom + "feed", (await OpdsTestHost.ReadFeedAsync(updates)).Root!.Name);
    }

    [Fact]
    public async Task CoversDoNotCountAsReadingAndCanRevalidate()
    {
        var (user, _, comic, seriesEntry) = await UserWithComicAsync(("p1.jpg", "not really an image"));
        var id = await _host.FileIdAsync(comic);
        var thumbnail = Href(seriesEntry, ThumbnailRel);
        Assert.EndsWith($"/issues/{id}/cover", thumbnail, StringComparison.Ordinal);

        using var cover = await _host.GetAsync(thumbnail!, user.UserName, OpdsTestHost.Password);
        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
        Assert.True(cover.Headers.CacheControl!.Private);
        Assert.Null(await _host.ProgressAsync(user.Id, comic));

        using var again = await _host.GetAsync(thumbnail!, user.UserName, OpdsTestHost.Password, configure: r => r.Headers.IfNoneMatch.Add(cover.Headers.ETag!));

        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
    }

    [Fact]
    public async Task APageRevalidatesAndStillCountsAsReading()
    {
        var (user, _, comic, _) = await UserWithComicAsync(("p1.jpg", "first"), ("p2.jpg", "second"));
        var id = await _host.FileIdAsync(comic);

        using var first = await _host.GetAsync($"/opds/v1.2/issues/{id}/pages/0", user.UserName, OpdsTestHost.Password);
        Assert.True(first.Headers.CacheControl!.Private);
        Assert.Equal(1, (await _host.ProgressAsync(user.Id, comic))!.CurrentPage);

        await using (var db = await _host.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ComicMaintainer.Core.Data.ComicMaintainerDbContext>>().CreateDbContextAsync())
        {
            var row = db.ReadingProgresses.Single(p => p.UserId == user.Id && p.ContentId == comic);
            db.ReadingProgresses.Remove(row);
            await db.SaveChangesAsync();
        }

        using var revalidated = await _host.GetAsync($"/opds/v1.2/issues/{id}/pages/0", user.UserName, OpdsTestHost.Password, configure: r => r.Headers.IfNoneMatch.Add(first.Headers.ETag!));

        Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
        Assert.Equal(1, (await _host.ProgressAsync(user.Id, comic))!.CurrentPage);
    }

    [Fact]
    public async Task TheFileIsServedInRanges()
    {
        var (user, _, comic, _) = await UserWithComicAsync(("p1.jpg", "first page"));
        var id = await _host.FileIdAsync(comic);
        var bytes = await File.ReadAllBytesAsync(comic);

        using var range = await _host.GetAsync($"/opds/v1.2/issues/{id}/file", user.UserName, OpdsTestHost.Password, configure: r => r.Headers.Range = new RangeHeaderValue(0, 9));

        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(bytes[..10], await range.Content.ReadAsByteArrayAsync());
        Assert.Equal(new ContentRangeHeaderValue(0, 9, bytes.Length), range.Content.Headers.ContentRange);
        Assert.Equal(Path.GetFileName(comic), range.Content.Headers.ContentDisposition!.FileNameStar ?? range.Content.Headers.ContentDisposition.FileName);

        Assert.Null(await _host.ProgressAsync(user.Id, comic));

        using var head = await _host.GetAsync($"/opds/v1.2/issues/{id}/file", user.UserName, OpdsTestHost.Password, method: HttpMethod.Head);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(bytes.Length, head.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task AFileOutsideTheLibraryIsNeverServed()
    {
        var user = await _host.CreateUserAsync();
        var outside = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".cbz");
        await File.WriteAllBytesAsync(outside, [1, 2, 3]);
        try
        {
            var outsideId = await _host.AddTrackedRowAsync(outside);
            var duplicate = Path.Join(_host.DuplicateDirectory, Guid.NewGuid().ToString("N") + ".cbz");
            await File.WriteAllBytesAsync(duplicate, [1, 2, 3]);
            var duplicateId = await _host.AddTrackedRowAsync(duplicate);
            var notAComic = await _host.AddTrackedRowAsync(Path.Join(_host.WatchedDirectory, "notes.txt"));

            foreach (var id in new[] { outsideId, duplicateId, notAComic, int.MaxValue })
            {
                foreach (var route in new[] { "file", "pages/0", "cover" })
                {
                    using var response = await _host.GetAsync($"/opds/v1.2/issues/{id}/{route}", user.UserName, OpdsTestHost.Password);
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                }
            }
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task AnIdCannotBeAPath()
    {
        var user = await _host.CreateUserAsync();

        using var response = await _host.GetAsync("/opds/v1.2/issues/%2Fetc%2Fpasswd/file", user.UserName, OpdsTestHost.Password);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownOpdsPathIsNotTheAppShell()
    {
        var user = await _host.CreateUserAsync();

        using var response = await _host.GetAsync("/opds/v1.2/nowhere", user.UserName, OpdsTestHost.Password);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task AnUnknownSeriesIsNotFound()
    {
        var user = await _host.CreateUserAsync();

        using var response = await _host.GetAsync("/opds/v1.2/series/" + Guid.NewGuid().ToString("N"), user.UserName, OpdsTestHost.Password);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%20")]
    public async Task ASearchNeedsATerm(string term)
    {
        var user = await _host.CreateUserAsync();

        using var response = await _host.GetAsync($"/opds/v1.2/search?q={term}", user.UserName, OpdsTestHost.Password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task AnOverlongSearchIsRefused()
    {
        var user = await _host.CreateUserAsync();

        using var response = await _host.GetAsync($"/opds/v1.2/search?q={new string('x', 201)}", user.UserName, OpdsTestHost.Password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheSearchDescriptionPointsAtTheSearch()
    {
        var user = await _host.CreateUserAsync();

        using var response = await _host.GetAsync("/opds/v1.2/opensearch.xml", user.UserName, OpdsTestHost.Password);

        Assert.StartsWith("application/opensearchdescription+xml", response.Content.Headers.ContentType!.ToString());
        var description = XDocument.Parse(await response.Content.ReadAsStringAsync());
        XNamespace openSearch = "http://a9.com/-/spec/opensearch/1.1/";
        var url = description.Root!.Element(openSearch + "Url")!;
        Assert.Equal("application/atom+xml;profile=opds-catalog;kind=acquisition", url.Attribute("type")!.Value);
        Assert.Equal("/opds/v1.2/search?q={searchTerms}", url.Attribute("template")!.Value);
    }

    [Fact]
    public async Task TitlesAreEscaped()
    {
        var user = await _host.CreateUserAsync();
        var series = "Tom & Jerry's " + Guid.NewGuid().ToString("N")[..6];
        await _host.AddComicAsync(Path.Join(series, series + " - Chapter 1.cbz"), ("p1.jpg", "a"));

        var feed = await SearchFeedAsync(user, series);

        Assert.Equal(series, Entries(feed).Single().Element(Atom + "title")!.Value);
    }

    [Fact]
    public async Task ALargeSeriesAndTheLibraryArePagedBothWays()
    {
        var user = await _host.CreateUserAsync();
        var series = UniqueSeries();
        for (var chapter = 1; chapter <= 51; chapter++)
        {
            await _host.AddComicAsync(Path.Join(series, $"{series} - Chapter {chapter}.cbz"), ("p1.jpg", "a"));
        }

        var seriesFeedHref = Href(EntryTitled(await SearchFeedAsync(user, series), series), "subsection")!;

        using var firstPage = await _host.GetAsync(seriesFeedHref, user.UserName, OpdsTestHost.Password);
        var first = await OpdsTestHost.ReadFeedAsync(firstPage);
        Assert.Equal(50, Entries(first).Count);
        Assert.Null(Href(first.Root!, "previous"));
        var next = Href(first.Root!, "next")!;
        Assert.EndsWith("?page=2", next, StringComparison.Ordinal);

        using var secondPage = await _host.GetAsync(next, user.UserName, OpdsTestHost.Password);
        var second = await OpdsTestHost.ReadFeedAsync(secondPage);
        Assert.Single(Entries(second));
        Assert.Null(Href(second.Root!, "next"));
        Assert.Equal(seriesFeedHref, Href(second.Root!, "previous"));

        using var all = await _host.GetAsync("/opds/v1.2/series", user.UserName, OpdsTestHost.Password);
        var library = await OpdsTestHost.ReadFeedAsync(all);
        Assert.Equal(Atom + "feed", library.Root!.Name);
    }

    [Fact]
    public async Task FeedsAreNotStoredByCaches()
    {
        var user = await _host.CreateUserAsync();

        using var feed = await _host.GetAsync(Catalog, user.UserName, OpdsTestHost.Password);

        Assert.True(feed.Headers.CacheControl!.NoStore);
    }
}

/// <summary>The catalog in the configurations other than on at the domain root.</summary>
[Trait("Category", "Integration")]
public sealed class OpdsCatalogModesIntegrationTests
{
    private const string Catalog = "/opds/v1.2/catalog";
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private const string StreamRel = "http://vaemendis.net/opds-pse/stream";
    private const string AcquisitionRel = "http://opds-spec.org/acquisition/open-access";

    [Theory]
    [InlineData("/opds/v1.2/catalog")]
    [InlineData("/opds/v1.2/series")]
    [InlineData("/opds/v1.2/issues/1/file")]
    [InlineData("/OPDS/v1.2/catalog")]
    public async Task WhileOffEveryRouteAnswers404WithoutAChallenge(string path)
    {
        await using var host = new OpdsTestHost(enabled: false);
        var user = await host.CreateUserAsync();

        using var anonymous = await host.GetAsync(path);
        using var signedIn = await host.GetAsync(path, user.UserName, OpdsTestHost.Password);

        Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);
        Assert.Empty(anonymous.Headers.WwwAuthenticate);
        Assert.Equal(HttpStatusCode.NotFound, signedIn.StatusCode);
    }

    [Theory]
    [InlineData("/comics")]
    [InlineData("")]
    public async Task UnderABasePathEveryLinkCarriesIt(string requestPrefix)
    {
        await using var host = new OpdsTestHost(basePath: "/comics");
        var user = await host.CreateUserAsync();
        var series = "Saga " + Guid.NewGuid().ToString("N")[..8];
        var comic = await host.AddComicAsync(Path.Join(series, series + " - Chapter 1.cbz"), ("p1.jpg", "first page"), ("p2.jpg", "second page"));

        using var catalogResponse = await host.GetAsync(requestPrefix + Catalog, user.UserName, OpdsTestHost.Password);
        var catalog = await OpdsTestHost.ReadFeedAsync(catalogResponse);
        var allHrefs = catalog.Descendants(Atom + "link").Select(l => l.Attribute("href")!.Value).ToList();
        Assert.NotEmpty(allHrefs);
        Assert.All(allHrefs, href => Assert.StartsWith("/comics/opds/v1.2/", href, StringComparison.Ordinal));

        // The links work as the proxy would present them, under the prefix.
        using var search = await host.GetAsync($"/comics/opds/v1.2/search?q={Uri.EscapeDataString(series)}", user.UserName, OpdsTestHost.Password);
        var entry = (await OpdsTestHost.ReadFeedAsync(search)).Root!.Elements(Atom + "entry").Single();
        var seriesHref = entry.Elements(Atom + "link").Single(l => (string?)l.Attribute("rel") == "subsection").Attribute("href")!.Value;
        Assert.StartsWith("/comics/opds/v1.2/series/", seriesHref, StringComparison.Ordinal);

        using var issuesResponse = await host.GetAsync(seriesHref, user.UserName, OpdsTestHost.Password);
        var issue = (await OpdsTestHost.ReadFeedAsync(issuesResponse)).Root!.Elements(Atom + "entry").Single();
        var streamHref = issue.Elements(Atom + "link").Single(l => (string?)l.Attribute("rel") == StreamRel).Attribute("href")!.Value;
        var fileHref = issue.Elements(Atom + "link").Single(l => (string?)l.Attribute("rel") == AcquisitionRel).Attribute("href")!.Value;

        using var page = await host.GetAsync(streamHref.Replace("{pageNumber}", "0"), user.UserName, OpdsTestHost.Password);
        Assert.Equal("first page", await page.Content.ReadAsStringAsync());

        using var download = await host.GetAsync(fileHref, user.UserName, OpdsTestHost.Password);
        Assert.Equal(await File.ReadAllBytesAsync(comic), await download.Content.ReadAsByteArrayAsync());

        using var description = await host.GetAsync("/comics/opds/v1.2/opensearch.xml", user.UserName, OpdsTestHost.Password);
        Assert.Contains("template=\"/comics/opds/v1.2/search?q={searchTerms}\"", await description.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithAutheliaOnlyAnApiKeyWorksHere()
    {
        await using var host = new OpdsTestHost(authelia: true);
        var user = await host.CreateUserAsync();
        var key = await host.CreateApiKeyAsync(user.Id);

        using var password = await host.GetAsync(Catalog, user.UserName, OpdsTestHost.Password);
        using var apiKey = await host.GetAsync(Catalog, user.UserName, key);

        Assert.Equal(HttpStatusCode.Unauthorized, password.StatusCode);
        Assert.Equal(OpdsBasicAuthenticationHandler.Challenge, password.Headers.WwwAuthenticate.ToString());
        Assert.Equal(HttpStatusCode.OK, apiKey.StatusCode);
    }

    [Fact]
    public async Task WithAutheliaAnAccountWithoutAPasswordSignsInWithItsKey()
    {
        await using var host = new OpdsTestHost(authelia: true);
        var user = new ApplicationUser { UserName = "sso-" + Guid.NewGuid().ToString("N")[..8], Email = "sso@example.test", IsActive = true };
        using (var scope = host.Services.CreateScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user)).Succeeded);
        }

        var key = await host.CreateApiKeyAsync(user.Id);

        using var response = await host.GetAsync(Catalog, user.UserName, key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
