using System.Net;
using System.Text.Json.Serialization;
using ComicMaintainer.MauiApp.Extensions;
using ComicMaintainer.MauiApp.Models;
using ComicMaintainer.MauiApp.Services;

namespace ComicMaintainer.Tests.AndroidApp
{
    public sealed class ServerAddressTests
    {
        [Theory]
        [InlineData("https://comics.example.com", "https://comics.example.com/")]
        [InlineData("http://192.168.1.100:5000/", "http://192.168.1.100:5000/")]
        [InlineData("  https://example.com/comics  ", "https://example.com/comics/")]
        public void TryParse_NormalizesAUsableAddress(string input, string expected)
        {
            Assert.True(ServerAddress.TryParse(input, out var normalized, out var error), error);
            Assert.Equal(expected, normalized);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("   ")]
        [InlineData("192.168.1.100:5000")]
        [InlineData("localhost:5000")]
        [InlineData("comics.example.com")]
        [InlineData("ftp://comics.example.com")]
        [InlineData("https://user:secret@comics.example.com")]
        [InlineData("https://comics.example.com/?x=1")]
        [InlineData("https://comics.example.com/#top")]
        public void TryParse_RefusesAnythingElseWithAReason(string? input)
        {
            Assert.False(ServerAddress.TryParse(input, out _, out var error));
            Assert.NotEmpty(error);
        }

        [Theory]
        [InlineData("http://192.168.1.100:5000", true)]
        [InlineData("https://comics.example.com", false)]
        [InlineData("not an address", false)]
        public void DescribeRisk_WarnsOnlyAboutPlainHttp(string input, bool warns)
        {
            Assert.Equal(warns ? ServerAddress.CleartextWarning : string.Empty, ServerAddress.DescribeRisk(input));
        }
    }

    public sealed class ApiCredentialTests
    {
        [Theory]
        [InlineData(CredentialKind.Session)]
        [InlineData(CredentialKind.ApiKey)]
        public void Serialize_RoundTrips(CredentialKind kind)
        {
            var restored = ApiCredential.Deserialize(new ApiCredential(kind, "secret", "https://a.test/").Serialize());

            Assert.Equal(kind, restored!.Kind);
            Assert.Equal("secret", restored.Value);
            Assert.Equal("https://a.test/", restored.ServerUrl);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("null")]
        [InlineData("""{"kind":"Session","server":"https://a.test/"}""")]
        [InlineData("""{"kind":"Session","value":"secret"}""")]
        [InlineData("""{"kind":"Nonsense","value":"secret","server":"https://a.test/"}""")]
        public void Deserialize_TreatsAnythingElseAsSignedOut(string? stored)
        {
            Assert.Null(ApiCredential.Deserialize(stored));
        }

        [Fact]
        public void ToString_DoesNotRevealTheSecret()
        {
            Assert.DoesNotContain("secret", new ApiCredential(CredentialKind.Session, "secret", "https://a.test/").ToString()!, StringComparison.Ordinal);
        }
    }

    public sealed class ComicFileTests
    {
        [Theory]
        [InlineData("/watched/Batman/Batman #1.cbz", "/watched/Batman", "Batman")]
        [InlineData(@"C:\Comics\Batman\Batman #1.cbz", @"C:\Comics\Batman", "Batman")]
        [InlineData("Batman #1.cbz", "", "/")]
        public void TheFolder_IsReadFromAServerPathOfEitherKind(string path, string directory, string folder)
        {
            var file = new ComicFile { FilePath = path };

            Assert.Equal(directory, file.Directory);
            Assert.Equal(folder, file.GetFolderName());
        }

        [Fact]
        public void LastModified_ReadsUnixSecondsAsLocalTime()
        {
            var file = new ComicFile { ModifiedUnixSeconds = 1_700_000_000 };

            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).LocalDateTime, file.LastModified);
        }

        [Theory]
        [InlineData(true, false, "Duplicate")]
        [InlineData(true, true, "Duplicate")]
        [InlineData(false, true, "Processed")]
        [InlineData(false, false, "Unprocessed")]
        public void GetStatus_PutsDuplicateFirst(bool duplicate, bool processed, string expected)
        {
            Assert.Equal(expected, new ComicFile { IsDuplicate = duplicate, IsProcessed = processed }.GetStatus());
        }
    }

    public sealed class ApiTransportTests
    {
        private const string Server = "https://comics.example.test/";

        private static (ApiTransport Transport, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            var handler = new StubHandler(respond);
            return (new ApiTransport(new StubHttpClients(handler)), handler);
        }

        private static Task<string> GetAsync(ApiTransport transport, ApiCredential? credential = null) =>
            transport.SendAsync(Server, HttpMethod.Get, "api/files", credential: credential);

        [Fact]
        public async Task ASessionTravelsAsABearerToken()
        {
            var (transport, handler) = Create(_ => StubHandler.Json("{}"));

            await GetAsync(transport, new ApiCredential(CredentialKind.Session, "the-token", Server));

            var request = Assert.Single(handler.Requests);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("the-token", request.Headers.Authorization.Parameter);
            Assert.False(request.Headers.Contains("X-API-Key"));
        }

        [Fact]
        public async Task AnApiKeyTravelsInItsOwnHeader()
        {
            var (transport, handler) = Create(_ => StubHandler.Json("{}"));

            await GetAsync(transport, new ApiCredential(CredentialKind.ApiKey, "cm_key", Server));

            var request = Assert.Single(handler.Requests);
            Assert.Equal("cm_key", Assert.Single(request.Headers.GetValues("X-API-Key")));
            Assert.Null(request.Headers.Authorization);
        }

        [Fact]
        public async Task WithoutACredential_NothingIsAttached()
        {
            var (transport, handler) = Create(_ => StubHandler.Json("{}"));

            await GetAsync(transport);

            var request = Assert.Single(handler.Requests);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-API-Key"));
        }

        [Fact]
        public async Task RequestsStayUnderAServerHostedAtASubPath()
        {
            var (transport, handler) = Create(_ => StubHandler.Json("{}"));

            await transport.SendAsync("https://example.test/comics/", HttpMethod.Get, "api/files?page=1");

            Assert.Equal("https://example.test/comics/api/files?page=1", Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, ApiErrorKind.Unauthorized)]
        [InlineData(HttpStatusCode.Forbidden, ApiErrorKind.Forbidden)]
        [InlineData(HttpStatusCode.NotFound, ApiErrorKind.NotFound)]
        [InlineData(HttpStatusCode.TooManyRequests, ApiErrorKind.RateLimited)]
        [InlineData(HttpStatusCode.BadRequest, ApiErrorKind.Rejected)]
        [InlineData(HttpStatusCode.InternalServerError, ApiErrorKind.Server)]
        [InlineData(HttpStatusCode.BadGateway, ApiErrorKind.Server)]
        [InlineData(HttpStatusCode.Found, ApiErrorKind.Redirected)]
        public async Task EveryFailureHasAKindAndAMessage(HttpStatusCode status, ApiErrorKind expected)
        {
            var (transport, _) = Create(_ => new HttpResponseMessage(status));

            var failure = await Assert.ThrowsAsync<ApiException>(() => GetAsync(transport));

            Assert.Equal(expected, failure.Kind);
            Assert.Equal(status, failure.StatusCode);
            Assert.NotEmpty(failure.Message);
        }

        [Theory]
        [InlineData("""{"error":"Invalid username or password"}""", "Invalid username or password")]
        [InlineData("""{"error":"Forbidden","message":"Insufficient permissions"}""", "Insufficient permissions")]
        [InlineData("""{"title":"Bad Request","detail":"Page is out of range","status":400}""", "Page is out of range")]
        [InlineData("""{"title":"Bad Request","status":400}""", "Bad Request")]
        [InlineData("\"A JSON string\"", "A JSON string")]
        [InlineData("File path is required", "File path is required")]
        public async Task TheServersOwnWords_AreShown(string body, string expected)
        {
            var (transport, _) = Create(_ => StubHandler.Text(body, HttpStatusCode.BadRequest));

            var failure = await Assert.ThrowsAsync<ApiException>(() => GetAsync(transport));

            Assert.Equal(expected, failure.Message);
        }

        [Theory]
        [InlineData("<html><body>Bad gateway</body></html>")]
        [InlineData("")]
        [InlineData("[1,2,3]")]
        [InlineData("""{"error":42}""")]
        public async Task AnythingElse_FallsBackToASafeMessage(string body)
        {
            var (transport, _) = Create(_ => StubHandler.Text(body, HttpStatusCode.BadRequest));

            var failure = await Assert.ThrowsAsync<ApiException>(() => GetAsync(transport));

            Assert.Equal("The server refused the request (HTTP 400).", failure.Message);
        }

        [Fact]
        public async Task AVeryLongBody_IsNotShown()
        {
            var (transport, _) = Create(_ => StubHandler.Text(new string('x', 2000), HttpStatusCode.InternalServerError));

            var failure = await Assert.ThrowsAsync<ApiException>(() => GetAsync(transport));

            Assert.DoesNotContain("xxx", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ARedirect_NamesOnlyTheHostItPointsTo()
        {
            var (transport, handler) = Create(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://auth.example.test/login?rd=secret-path");
                return response;
            });

            var failure = await Assert.ThrowsAsync<ApiException>(() => GetAsync(transport));

            Assert.Equal(ApiErrorKind.Redirected, failure.Kind);
            Assert.Contains("auth.example.test", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-path", failure.Message, StringComparison.Ordinal);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task ARelativeRedirect_IsStillExplained()
        {
            var (transport, _) = Create(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                response.Headers.Location = new Uri("/elsewhere", UriKind.Relative);
                return response;
            });

            var failure = await Assert.ThrowsAsync<ApiException>(() => GetAsync(transport));

            Assert.Contains("another address", failure.Message, StringComparison.Ordinal);
        }

        public static TheoryData<Exception> ConnectionFailures() =>
        [
            new HttpRequestException("refused"),
            new IOException("reset"),
            new System.Net.Sockets.SocketException(),
            new global::Java.IO.ConnectException(),
        ];

        [Theory]
        [MemberData(nameof(ConnectionFailures))]
        public async Task AConnectionFailure_IsReportedAsOne(Exception cause)
        {
            var (transport, _) = Create(_ => throw cause);

            var failure = await Assert.ThrowsAsync<ApiException>(() => GetAsync(transport));

            Assert.Equal(ApiErrorKind.Network, failure.Kind);
            Assert.Same(cause, failure.InnerException);
        }

        [Fact]
        public async Task AnUnexpectedException_IsNotDisguisedAsANetworkProblem()
        {
            var (transport, _) = Create(_ => throw new InvalidOperationException("a bug"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => GetAsync(transport));
        }

        [Fact]
        public async Task ATimeout_IsReportedAsANetworkProblem()
        {
            var (transport, _) = Create(_ => throw new TaskCanceledException());

            var failure = await Assert.ThrowsAsync<ApiException>(() => GetAsync(transport));

            Assert.Equal(ApiErrorKind.Network, failure.Kind);
            Assert.Contains("too long", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TheCallersCancellation_StaysACancellation()
        {
            var (transport, _) = Create(_ => StubHandler.Json("{}"));
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => transport.SendAsync(Server, HttpMethod.Get, "api/files", cancellationToken: cancelled.Token));
        }

        [Theory]
        [InlineData("<html>a proxy's login page</html>")]
        [InlineData("null")]
        public void AnUnreadableBody_IsExplained(string body)
        {
            var failure = Assert.Throws<ApiException>(() => ApiTransport.ParseJson(body, ApiJsonContext.Default.FilesPage));

            Assert.Equal(ApiErrorKind.InvalidResponse, failure.Kind);
        }
    }

    public sealed class SessionManagerTests
    {
        private const string Server = "https://comics.example.test/";
        private const string UserJson = """{"username":"alice","canModifyLibrary":true}""";

        private readonly InMemoryCredentialStore _store = new();
        private readonly InMemorySettings _settings = new();
        private readonly List<string?> _ended = [];

        private SessionManager Create(StubHandler handler)
        {
            var session = new SessionManager(new ApiTransport(new StubHttpClients(handler)), _store, _settings);
            session.SessionEnded += (_, e) => _ended.Add(e.Message);
            return session;
        }

        private ApiCredential StoreCredential(CredentialKind kind = CredentialKind.Session)
        {
            _settings.ServerUrl = Server;
            _store.Stored = new ApiCredential(kind, "secret", Server);
            return _store.Stored;
        }

        [Fact]
        public async Task Restore_WithNothingStored_AsksTheServerNothing()
        {
            var handler = new StubHandler(_ => throw new InvalidOperationException("no request expected"));

            Assert.Equal(SessionRestoreResult.SignedOut, await Create(handler).RestoreAsync());
            Assert.Empty(handler.Requests);
            Assert.Empty(_ended);
        }

        [Fact]
        public async Task Restore_ConfirmedByTheServer_IsSignedIn()
        {
            StoreCredential();
            var session = Create(new StubHandler(_ => StubHandler.Json(UserJson)));

            Assert.Equal(SessionRestoreResult.SignedIn, await session.RestoreAsync());
            Assert.Equal("alice", session.Account!.Username);
            Assert.Equal(CredentialKind.Session, session.CurrentKind);
        }

        // Being offline must not sign anyone out.
        [Theory]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        [InlineData(HttpStatusCode.BadGateway)]
        public async Task Restore_WhenTheServerCannotConfirm_KeepsTheCredential(HttpStatusCode status)
        {
            StoreCredential();

            Assert.Equal(SessionRestoreResult.Unverified, await Create(new StubHandler(_ => new HttpResponseMessage(status))).RestoreAsync());
            Assert.NotNull(_store.Stored);
            Assert.Empty(_ended);
        }

        [Fact]
        public async Task Restore_WhileOffline_KeepsTheCredential()
        {
            StoreCredential();

            Assert.Equal(SessionRestoreResult.Unverified, await Create(new StubHandler(_ => throw new HttpRequestException("offline"))).RestoreAsync());
            Assert.NotNull(_store.Stored);
        }

        // Thrown from the page's startup, a FormatException here would crash the app at every launch.
        [Theory]
        [InlineData(CredentialKind.Session, "not a valid bearer token")]
        [InlineData(CredentialKind.ApiKey, "damaged\nkey")]
        public async Task ADamagedStoredCredential_IsDiscardedInsteadOfCrashing(CredentialKind kind, string value)
        {
            _settings.ServerUrl = Server;
            _store.Stored = new ApiCredential(kind, value, Server);
            var handler = new StubHandler(_ => throw new InvalidOperationException("no request expected"));

            Assert.Equal(SessionRestoreResult.SignedOut, await Create(handler).RestoreAsync());

            Assert.Empty(handler.Requests);
            Assert.Null(_store.Stored);
            Assert.Single(_ended);
        }

        [Fact]
        public async Task ACredentialIsNeverSentToAnotherServer()
        {
            StoreCredential();
            _settings.ServerUrl = "https://other.example.test/";
            var handler = new StubHandler(_ => StubHandler.Json(UserJson));

            Assert.Equal(SessionRestoreResult.SignedOut, await Create(handler).RestoreAsync());

            Assert.Empty(handler.Requests);
            Assert.Null(_store.Stored);
        }

        [Fact]
        public async Task SignIn_WithAnUnusableAddress_SendsNothing()
        {
            var handler = new StubHandler(_ => throw new InvalidOperationException("no request expected"));

            var failure = await Assert.ThrowsAsync<ApiException>(() => Create(handler).SignInAsync("192.168.1.100:5000", "a", "b"));

            Assert.Equal(ApiErrorKind.InvalidAddress, failure.Kind);
            Assert.Empty(handler.Requests);
        }

        // A line break makes the HTTP client throw a FormatException, which would crash the app on a bad paste.
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("cm_two words")]
        [InlineData("cm_line\nbreak")]
        [InlineData("cm_ключ")]
        public async Task AnApiKeyThatCannotBeAHeader_IsRefusedWithoutARequest(string key)
        {
            var handler = new StubHandler(_ => throw new InvalidOperationException("no request expected"));

            var failure = await Assert.ThrowsAsync<ApiException>(() => Create(handler).SignInWithApiKeyAsync(Server, key));

            Assert.Equal(ApiErrorKind.Unauthorized, failure.Kind);
            Assert.Equal(SessionManager.InvalidApiKeyMessage, failure.Message);
            Assert.Empty(handler.Requests);
            Assert.Null(_store.Stored);
        }

        [Fact]
        public async Task SignIn_WithoutATokenInTheAnswer_DoesNotSignIn()
        {
            var failure = await Assert.ThrowsAsync<ApiException>(() => Create(new StubHandler(_ => StubHandler.Json("{}"))).SignInAsync(Server, "a", "b"));

            Assert.Equal(ApiErrorKind.InvalidResponse, failure.Kind);
            Assert.Null(_store.Stored);
        }

        [Fact]
        public async Task SignIn_WhenTheDeviceCannotStoreIt_IsNotSignedIn()
        {
            _store.SaveFailure = new ApiException(ApiErrorKind.CredentialStorage, "could not store");
            var session = Create(new StubHandler(request =>
                StubHandler.Json(request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal) ? """{"token":"t"}""" : UserJson)));

            var failure = await Assert.ThrowsAsync<ApiException>(() => session.SignInAsync(Server, "a", "b"));

            Assert.Equal(ApiErrorKind.CredentialStorage, failure.Kind);
            Assert.Null(session.CurrentKind);
            Assert.Empty(_settings.ServerUrl);
        }

        [Fact]
        public async Task SignIn_RefusedWhenTheSetupCheckAlsoFails_KeepsTheServersMessage()
        {
            var session = Create(new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? StubHandler.Json("""{"error":"Invalid username or password"}""", HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError)));

            var failure = await Assert.ThrowsAsync<ApiException>(() => session.SignInAsync(Server, "a", "b"));

            Assert.Equal("Invalid username or password", failure.Message);
        }

        [Fact]
        public async Task SigningInAgain_IsNotUndoneByALateRefusalOfTheOldCredential()
        {
            var old = StoreCredential();
            var session = Create(new StubHandler(request =>
                StubHandler.Json(request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal) ? """{"token":"new"}""" : UserJson)));
            await session.RestoreAsync();
            await session.SignInAsync(Server, "a", "b");

            await session.NotifyUnauthorizedAsync(old);

            Assert.Equal("new", _store.Stored!.Value);
            Assert.Empty(_ended);
        }

        [Fact]
        public async Task SignOut_WhenTheServerCannotBeReached_ClearsLocallyAndSaysSo()
        {
            StoreCredential();
            var session = Create(new StubHandler(_ => throw new HttpRequestException("offline")));

            await session.SignOutAsync();

            Assert.Null(_store.Stored);
            Assert.Equal(SessionManager.ServerNotReachedNotice, Assert.Single(_ended));
        }

        [Fact]
        public async Task SignOut_OfASessionTheServerAlreadyEnded_IsNotAProblem()
        {
            StoreCredential();

            await Create(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))).SignOutAsync();

            Assert.Null(_store.Stored);
            Assert.Null(Assert.Single(_ended));
        }

        [Fact]
        public async Task SignOut_GoesToTheServerThatIssuedTheCredential()
        {
            StoreCredential();
            _settings.ServerUrl = "https://new.example.test/";
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

            await Create(handler).SignOutAsync();

            Assert.Equal("comics.example.test", Assert.Single(handler.Requests).RequestUri!.Host);
        }

        [Fact]
        public async Task SignOut_WithAnApiKey_LeavesItAloneOnTheServer()
        {
            StoreCredential(CredentialKind.ApiKey);
            var handler = new StubHandler(_ => throw new InvalidOperationException("no request expected"));

            await Create(handler).SignOutAsync();

            Assert.Empty(handler.Requests);
            Assert.Equal(SessionManager.ApiKeyStillValidNotice, Assert.Single(_ended));
        }

        [Fact]
        public async Task RequireCredential_WithoutOne_EndsTheSessionVisibly()
        {
            var session = Create(new StubHandler(_ => StubHandler.Json("{}")));

            var failure = await Assert.ThrowsAsync<ApiException>(session.RequireCredentialAsync);

            Assert.Equal(ApiErrorKind.Unauthorized, failure.Kind);
            Assert.Equal(SessionManager.SignInRequiredMessage, Assert.Single(_ended));
        }
    }

    public sealed class ApiServiceTests
    {
        private const string Server = "https://comics.example.test/";

        private static ApiService Create(StubHandler handler, InMemoryCredentialStore? store = null)
        {
            var settings = new InMemorySettings { ServerUrl = Server };
            store ??= new InMemoryCredentialStore { Stored = new ApiCredential(CredentialKind.Session, "t", Server) };
            var transport = new ApiTransport(new StubHttpClients(handler));
            return new ApiService(transport, new SessionManager(transport, store, settings));
        }

        [Fact]
        public async Task TheLibraryQuery_UsesTheServersParameterNamesAndEscapesValues()
        {
            var handler = new StubHandler(_ => StubHandler.Json("""{"files":[],"page":1,"total_pages":1,"total_files":0}"""));

            await Create(handler).GetFilesAsync(page: 3, perPage: 25, filter: "unmarked", search: "Spider & Man");

            Assert.Equal(
                "/api/files?page=3&per_page=25&filter=unmarked&search=Spider%20%26%20Man",
                Assert.Single(handler.Requests).RequestUri!.PathAndQuery);
        }

        [Fact]
        public async Task TheLibraryQuery_LeavesOutAnEmptyFilterAndSearch()
        {
            var handler = new StubHandler(_ => StubHandler.Json("""{"files":[],"page":1,"total_pages":1,"total_files":0}"""));

            await Create(handler).GetFilesAsync(filter: " ", search: "");

            Assert.Equal("/api/files?page=1&per_page=100", Assert.Single(handler.Requests).RequestUri!.PathAndQuery);
        }

        [Fact]
        public async Task ProcessingAFile_PutsItsPathInTheQuery()
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

            await Create(handler).ProcessFileAsync("/watched/A & B #1.cbz");

            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/files/process?filePath=%2Fwatched%2FA%20%26%20B%20%231.cbz", request.RequestUri!.PathAndQuery);
            Assert.Null(request.Content);
        }

        [Fact]
        public async Task DeletingAFile_EncodesItsPathAsUrlSafeBase64WithoutPadding()
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

            // These bytes encode to base64 containing '+', '/' and padding.
            await Create(handler).DeleteFileAsync("a?>~");

            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("/api/files/YT8-fg", request.RequestUri!.AbsolutePath);
        }

        [Fact]
        public async Task ABatchAnswerWithoutAJobId_IsNotTreatedAsStarted()
        {
            var failure = await Assert.ThrowsAsync<ApiException>(
                () => Create(new StubHandler(_ => StubHandler.Json("{}"))).StartBatchProcessAsync(["a"]));

            Assert.Equal(ApiErrorKind.InvalidResponse, failure.Kind);
        }

        [Fact]
        public async Task JobStatus_OtherFailuresAreNotSwallowed()
        {
            await Assert.ThrowsAsync<ApiException>(
                () => Create(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))).GetJobStatusAsync("x"));
        }

        [Fact]
        public async Task TestConnection_ExplainsAnUnusableAddressWithoutARequest()
        {
            var handler = new StubHandler(_ => throw new InvalidOperationException("no request expected"));

            var result = await Create(handler).TestConnectionAsync("not an address");

            Assert.False(result.Success);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task TestConnection_DoesNotMistakeSomeOtherJsonForTheServer()
        {
            var result = await Create(new StubHandler(_ => StubHandler.Json("""{"name":"router"}"""))).TestConnectionAsync(Server);

            Assert.False(result.Success);
            Assert.Contains("not a ComicMaintainer server", result.Message, StringComparison.Ordinal);
        }

        // The old test accepted any 2xx, so a proxy's login page reported "connected".
        [Fact]
        public async Task TestConnection_DoesNotMistakeAWebPageForTheServer()
        {
            var result = await Create(new StubHandler(_ => StubHandler.Text("<html>Sign in</html>", HttpStatusCode.OK))).TestConnectionAsync(Server);

            Assert.False(result.Success);
        }

        [Fact]
        public async Task TestConnection_ReportsAnUnreachableServerAsAResult()
        {
            var result = await Create(new StubHandler(_ => throw new HttpRequestException("refused"))).TestConnectionAsync(Server);

            Assert.False(result.Success);
            Assert.Contains("Could not reach", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheContractTypes_CarryTheServersJsonNames()
        {
            static string? NameOf(Type type, string property) =>
                ((JsonPropertyNameAttribute?)Attribute.GetCustomAttribute(type.GetProperty(property)!, typeof(JsonPropertyNameAttribute)))?.Name;

            Assert.Equal("relative_path", NameOf(typeof(ComicFile), nameof(ComicFile.FilePath)));
            Assert.Equal("total_pages", NameOf(typeof(FilesPage), nameof(FilesPage.TotalPages)));
            Assert.Equal("job_id", NameOf(typeof(JobStatusInfo), nameof(JobStatusInfo.JobId)));
        }
    }
}

namespace Java.IO
{
    /// <summary>Stands in for the Java exceptions the Android HTTP handler fails with, which are matched by name.</summary>
    internal sealed class ConnectException : Exception;
}
