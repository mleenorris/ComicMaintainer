using System.Net;
using ComicMaintainer.MauiApp.Models;
using ComicMaintainer.MauiApp.Services;

namespace ComicMaintainer.Tests.AndroidApp;

/// <summary>Keeps the credential in memory, in place of the device's SecureStorage.</summary>
internal sealed class InMemoryCredentialStore : ICredentialStore
{
    public ApiCredential? Stored { get; set; }

    public int Saves { get; private set; }

    public Exception? SaveFailure { get; set; }

    public Task<ApiCredential?> LoadAsync() => Task.FromResult(Stored);

    public Task SaveAsync(ApiCredential credential)
    {
        if (SaveFailure is not null)
        {
            throw SaveFailure;
        }

        Saves++;
        Stored = credential;
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        Stored = null;
        return Task.CompletedTask;
    }
}

internal sealed class InMemorySettings : ISettingsService
{
    public string ServerUrl { get; set; } = string.Empty;
}

/// <summary>Records every request and answers from <see cref="Respond"/>.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        Respond = respond;
    }

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; }

    public List<HttpRequestMessage> Requests { get; } = [];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Text(string text, HttpStatusCode status) =>
        new(status) { Content = new StringContent(text) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(Respond(request));
    }
}

/// <summary>A server that takes the request and never answers, as an unreachable one can behave.</summary>
internal sealed class SilentHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("A delay without an end only finishes by being cancelled.");
    }
}

internal sealed class StubHttpClients(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        Assert.Equal(ApiTransport.HttpClientName, name);
        return new HttpClient(handler, disposeHandler: false);
    }
}

internal sealed class RecordingDialogs : IUserDialogs
{
    public List<string> Alerts { get; } = [];

    public List<string> Questions { get; } = [];

    public bool ConfirmAnswer { get; set; } = true;

    public string? ChosenAction { get; set; }

    public Task ShowAlertAsync(string title, string message)
    {
        Alerts.Add(message);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
    {
        Questions.Add(message);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<string?> ChooseActionAsync(string title, string cancel, params string[] actions) => Task.FromResult(ChosenAction);
}

internal sealed class RecordingNavigator : INavigator
{
    public List<string> Pages { get; } = [];

    public Task ShowSignInAsync()
    {
        Pages.Add("sign-in");
        return Task.CompletedTask;
    }

    public Task ShowLibraryAsync()
    {
        Pages.Add("library");
        return Task.CompletedTask;
    }
}

internal sealed class FakeTheme : IThemeService
{
    public bool IsDarkMode { get; set; }
}

/// <summary>An <see cref="ISessionManager"/> whose outcomes the test sets.</summary>
internal sealed class FakeSession : ISessionManager
{
    public event EventHandler<SessionEndedEventArgs>? SessionEnded;

    public AccountInfo? Account { get; set; }

    public CredentialKind? CurrentKind { get; set; }

    public SessionRestoreResult RestoreResult { get; set; } = SessionRestoreResult.SignedOut;

    public ApiException? SignInFailure { get; set; }

    public int SignOuts { get; private set; }

    /// <summary>When set, signing out waits for this.</summary>
    public TaskCompletionSource? HeldSignOut { get; set; }

    public (string Server, string Username, string Password)? PasswordSignIn { get; private set; }

    public (string Server, string ApiKey)? ApiKeySignIn { get; private set; }

    public void EndSession(string? message) => SessionEnded?.Invoke(this, new SessionEndedEventArgs(message));

    public Task<SessionRestoreResult> RestoreAsync(CancellationToken cancellationToken = default) => Task.FromResult(RestoreResult);

    public Task SignInAsync(string serverUrl, string username, string password, CancellationToken cancellationToken = default)
    {
        PasswordSignIn = (serverUrl, username, password);
        return SignInFailure is null ? Task.CompletedTask : Task.FromException(SignInFailure);
    }

    public Task SignInWithApiKeyAsync(string serverUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        ApiKeySignIn = (serverUrl, apiKey);
        return SignInFailure is null ? Task.CompletedTask : Task.FromException(SignInFailure);
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        SignOuts++;
        return HeldSignOut?.Task ?? Task.CompletedTask;
    }

    public Task<ApiCredential> RequireCredentialAsync() => throw new NotSupportedException();

    public Task NotifyUnauthorizedAsync(ApiCredential credential) => throw new NotSupportedException();
}

/// <summary>An <see cref="IApiService"/> that records calls and answers as the test sets.</summary>
internal sealed class FakeApi : IApiService
{
    public FilesPage Page { get; set; } = new() { Page = 1, TotalPages = 1 };

    public ApiException? Failure { get; set; }

    public ConnectionTestResult ConnectionResult { get; set; } = new(true, "Reached ComicMaintainer 3.0.0.");

    public List<(int Page, string? Filter, string? Search)> Loads { get; } = [];

    /// <summary>Answers for the next loads, oldest first, which arrive when the test completes them.</summary>
    public Queue<TaskCompletionSource<FilesPage>> HeldLoads { get; } = new();

    public List<string> TestedAddresses { get; } = [];

    /// <summary>When set, a connection test waits for this.</summary>
    public TaskCompletionSource<ConnectionTestResult>? HeldConnectionTest { get; set; }

    public List<string> Processed { get; } = [];

    public List<string> Deleted { get; } = [];

    public List<IReadOnlyCollection<string>> BatchProcessed { get; } = [];

    public List<IReadOnlyCollection<string>> BatchDeleted { get; } = [];

    public Task<ConnectionTestResult> TestConnectionAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        TestedAddresses.Add(serverUrl);
        return HeldConnectionTest?.Task ?? Task.FromResult(ConnectionResult);
    }

    public Task<FilesPage> GetFilesAsync(int page = 1, int perPage = 100, string? filter = null, string? search = null, CancellationToken cancellationToken = default)
    {
        Loads.Add((page, filter, search));
        if (HeldLoads.TryDequeue(out var held))
        {
            return held.Task;
        }

        return Failure is null ? Task.FromResult(Page) : Task.FromException<FilesPage>(Failure);
    }

    public Task ProcessFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        Processed.Add(filePath);
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }

    public Task<string> StartBatchProcessAsync(IReadOnlyCollection<string> files, CancellationToken cancellationToken = default)
    {
        BatchProcessed.Add(files);
        return Failure is null ? Task.FromResult("job") : Task.FromException<string>(Failure);
    }

    public Task DeleteFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        Deleted.Add(filePath);
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }

    public Task<string> StartBatchDeleteAsync(IReadOnlyCollection<string> files, CancellationToken cancellationToken = default)
    {
        BatchDeleted.Add(files);
        return Failure is null ? Task.FromResult("job") : Task.FromException<string>(Failure);
    }

    public Task<JobStatusInfo?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult<JobStatusInfo?>(null);
}
