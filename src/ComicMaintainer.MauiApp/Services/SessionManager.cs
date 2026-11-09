using System.Net.Http.Json;
using ComicMaintainer.MauiApp.Models;

namespace ComicMaintainer.MauiApp.Services;

/// <inheritdoc cref="ISessionManager"/>
public sealed class SessionManager : ISessionManager
{
    internal const string SessionExpiredMessage =
        "Your session has ended: it expired, your password changed, or you were signed out elsewhere. Sign in again.";

    internal const string ApiKeyRejectedMessage =
        "The server no longer accepts this API key. Sign in with your password or create a new key.";

    internal const string SignInRequiredMessage = "Sign in to continue.";

    internal const string SetupRequiredMessage =
        "This server has not been set up yet. Open it in a web browser to create the administrator account, then sign in here.";

    internal const string InvalidApiKeyMessage = "The server did not accept this API key.";

    internal const string ServerNotReachedNotice =
        "Signed out on this device, but the server could not be reached, so the session stays valid there until it expires.";

    internal const string ApiKeyStillValidNotice =
        "Signed out on this device. The API key keeps working until you revoke it on the server.";

    private readonly ApiTransport _transport;
    private readonly ICredentialStore _store;
    private readonly ISettingsService _settings;
    private ApiCredential? _current;
    private bool _loaded;

    public SessionManager(ApiTransport transport, ICredentialStore store, ISettingsService settings)
    {
        _transport = transport;
        _store = store;
        _settings = settings;
    }

    /// <inheritdoc/>
    public event EventHandler<SessionEndedEventArgs>? SessionEnded;

    /// <inheritdoc/>
    public AccountInfo? Account { get; private set; }

    /// <inheritdoc/>
    public CredentialKind? CurrentKind => _current?.Kind;

    /// <inheritdoc/>
    public async Task<SessionRestoreResult> RestoreAsync(CancellationToken cancellationToken = default)
    {
        var credential = await GetCredentialAsync();
        if (credential is null)
        {
            return SessionRestoreResult.SignedOut;
        }

        try
        {
            Account = await FetchAccountAsync(credential, cancellationToken);
            return SessionRestoreResult.SignedIn;
        }
        catch (ApiException exception) when (exception.Kind == ApiErrorKind.Unauthorized)
        {
            await NotifyUnauthorizedAsync(credential);
            return SessionRestoreResult.SignedOut;
        }
        catch (ApiException)
        {
            return SessionRestoreResult.Unverified;
        }
    }

    /// <inheritdoc/>
    public async Task SignInAsync(string serverUrl, string username, string password, CancellationToken cancellationToken = default)
    {
        var server = ParseServer(serverUrl);
        using var content = JsonContent.Create(new LoginRequest(username, password), ApiJsonContext.Default.LoginRequest);

        string body;
        try
        {
            body = await _transport.SendAsync(server, HttpMethod.Post, "api/auth/login", content, cancellationToken: cancellationToken);
        }
        catch (ApiException exception) when (exception.Kind == ApiErrorKind.Unauthorized)
        {
            if (await IsSetupRequiredAsync(server, cancellationToken))
            {
                throw new ApiException(ApiErrorKind.SetupRequired, SetupRequiredMessage, exception.StatusCode, exception);
            }

            throw;
        }

        var token = ApiTransport.ParseJson(body, ApiJsonContext.Default.LoginResponse).Token;
        if (string.IsNullOrEmpty(token))
        {
            throw new ApiException(ApiErrorKind.InvalidResponse, "The server signed you in but sent no session token.");
        }

        var credential = new ApiCredential(CredentialKind.Session, token, server);
        await EstablishAsync(credential, await FetchAccountAsync(credential, cancellationToken));
    }

    /// <inheritdoc/>
    public async Task SignInWithApiKeyAsync(string serverUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        var server = ParseServer(serverUrl);
        var key = apiKey.Trim();

        // The key is pasted by hand and goes into an HTTP header, which cannot carry spaces, line breaks or non-ASCII text.
        if (key.Length == 0 || key.Any(character => character is <= ' ' or > '~'))
        {
            throw new ApiException(ApiErrorKind.Unauthorized, InvalidApiKeyMessage);
        }

        var credential = new ApiCredential(CredentialKind.ApiKey, key, server);

        AccountInfo account;
        try
        {
            account = await FetchAccountAsync(credential, cancellationToken);
        }
        catch (ApiException exception) when (exception.Kind == ApiErrorKind.Unauthorized)
        {
            throw new ApiException(ApiErrorKind.Unauthorized, InvalidApiKeyMessage, exception.StatusCode, exception);
        }

        await EstablishAsync(credential, account);
    }

    /// <inheritdoc/>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync();
        var credential = _current;
        string? notice = null;

        if (credential?.Kind == CredentialKind.ApiKey)
        {
            notice = ApiKeyStillValidNotice;
        }
        else if (credential is not null)
        {
            try
            {
                await _transport.SendAsync(credential.ServerUrl, HttpMethod.Post, "api/auth/logout", credential: credential, cancellationToken: cancellationToken);
            }
            catch (ApiException exception) when (exception.Kind != ApiErrorKind.Unauthorized)
            {
                notice = ServerNotReachedNotice;
            }
            catch (ApiException)
            {
                // The server had already ended this session, which is what signing out asks for.
            }
        }

        await ForgetAsync();
        SessionEnded?.Invoke(this, new SessionEndedEventArgs(notice));
    }

    /// <inheritdoc/>
    public async Task<ApiCredential> RequireCredentialAsync()
    {
        var credential = await GetCredentialAsync();
        if (credential is not null)
        {
            return credential;
        }

        SessionEnded?.Invoke(this, new SessionEndedEventArgs(SignInRequiredMessage));
        throw new ApiException(ApiErrorKind.Unauthorized, SignInRequiredMessage);
    }

    /// <inheritdoc/>
    public async Task NotifyUnauthorizedAsync(ApiCredential credential)
    {
        // A slow request made with an earlier credential must not end the session the user has signed in to since.
        if (!ReferenceEquals(_current, credential))
        {
            return;
        }

        await ForgetAsync();
        SessionEnded?.Invoke(
            this,
            new SessionEndedEventArgs(credential.Kind == CredentialKind.ApiKey ? ApiKeyRejectedMessage : SessionExpiredMessage));
    }

    private static string ParseServer(string serverUrl) =>
        ServerAddress.TryParse(serverUrl, out var server, out var error)
            ? server
            : throw new ApiException(ApiErrorKind.InvalidAddress, error);

    private async Task LoadAsync()
    {
        if (!_loaded)
        {
            _current = await _store.LoadAsync();
            _loaded = true;
        }
    }

    // A credential only ever goes to the server that issued it. Should the configured address no longer be that server,
    // the credential is useless and is dropped instead of being sent elsewhere.
    private async Task<ApiCredential?> GetCredentialAsync()
    {
        await LoadAsync();
        if (_current is not null && !string.Equals(_current.ServerUrl, _settings.ServerUrl, StringComparison.Ordinal))
        {
            await ForgetAsync();
        }

        return _current;
    }

    private async Task EstablishAsync(ApiCredential credential, AccountInfo account)
    {
        await _store.SaveAsync(credential);
        _settings.ServerUrl = credential.ServerUrl;
        _current = credential;
        _loaded = true;
        Account = account;
    }

    private async Task ForgetAsync()
    {
        _current = null;
        Account = null;
        await _store.ClearAsync();
    }

    private async Task<AccountInfo> FetchAccountAsync(ApiCredential credential, CancellationToken cancellationToken)
    {
        var body = await _transport.SendAsync(credential.ServerUrl, HttpMethod.Get, "api/auth/user", credential: credential, cancellationToken: cancellationToken);
        return ApiTransport.ParseJson(body, ApiJsonContext.Default.AccountInfo);
    }

    // Asked only after a refused sign-in: a server without accounts would otherwise just say the password is wrong.
    private async Task<bool> IsSetupRequiredAsync(string server, CancellationToken cancellationToken)
    {
        try
        {
            var body = await _transport.SendAsync(server, HttpMethod.Get, "api/auth/setup-required", cancellationToken: cancellationToken);
            return ApiTransport.ParseJson(body, ApiJsonContext.Default.SetupRequiredResponse).SetupRequired;
        }
        catch (ApiException)
        {
            return false;
        }
    }
}
