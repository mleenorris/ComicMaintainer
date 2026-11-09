using ComicMaintainer.MauiApp.Models;

namespace ComicMaintainer.MauiApp.Services;

/// <summary>What was found when the app looked for an earlier sign-in.</summary>
public enum SessionRestoreResult
{
    /// <summary>There is no usable credential: the user has to sign in.</summary>
    SignedOut,

    /// <summary>The server confirmed the stored credential.</summary>
    SignedIn,

    /// <summary>
    /// A credential is stored but the server could not confirm it, for example because the device is offline. It is
    /// kept: a network problem must not sign anyone out.
    /// </summary>
    Unverified,
}

/// <summary>Why a session ended, and what to tell the user. <see cref="Message"/> is null when there is nothing to say.</summary>
public sealed class SessionEndedEventArgs : EventArgs
{
    public SessionEndedEventArgs(string? message)
    {
        Message = message;
    }

    public string? Message { get; }
}

/// <summary>Signs in and out, keeps the credential, and notices when the server stops accepting it.</summary>
public interface ISessionManager
{
    /// <summary>
    /// Raised whenever the app stops being signed in: on sign-out, and when the server refuses the credential because
    /// it expired or was revoked (password change, sign-out elsewhere, disabled account). The user must be shown the
    /// sign-in page and the message.
    /// </summary>
    event EventHandler<SessionEndedEventArgs>? SessionEnded;

    /// <summary>The signed-in account, or null when signed out or not yet confirmed by the server.</summary>
    AccountInfo? Account { get; }

    /// <summary>The kind of credential in use, or null when signed out.</summary>
    CredentialKind? CurrentKind { get; }

    /// <summary>Looks for an earlier sign-in and asks the server whether it is still good.</summary>
    Task<SessionRestoreResult> RestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>Signs in with a password. The password is sent once and never stored.</summary>
    Task SignInAsync(string serverUrl, string username, string password, CancellationToken cancellationToken = default);

    /// <summary>Signs in with an API key created beforehand. Nothing is created or replaced on the server.</summary>
    Task SignInWithApiKeyAsync(string serverUrl, string apiKey, CancellationToken cancellationToken = default);

    /// <summary>Signs out here and, for a session, ends it on the server. A server that cannot be reached is reported, not fatal.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>The credential to sign a request with. Without one, ends the session and throws <see cref="ApiException"/>.</summary>
    Task<ApiCredential> RequireCredentialAsync();

    /// <summary>Reports that the server refused <paramref name="credential"/>, which ends the session if it is still the current one.</summary>
    Task NotifyUnauthorizedAsync(ApiCredential credential);
}
