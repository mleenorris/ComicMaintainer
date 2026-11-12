using System.Net;

namespace ComicMaintainer.MauiApp.Services;

/// <summary>Why a request to the server failed.</summary>
public enum ApiErrorKind
{
    /// <summary>The server does not accept the credentials: a failed sign-in, or a session that has ended.</summary>
    Unauthorized,

    /// <summary>The account is signed in but may not do this, such as a read-only account processing a file.</summary>
    Forbidden,

    /// <summary>Too many requests: the user has to wait, and retrying at once only makes it worse.</summary>
    RateLimited,

    /// <summary>The server answered with a redirect, which the app does not follow; see <see cref="ApiTransport"/>.</summary>
    Redirected,

    /// <summary>The server does not know what was asked for.</summary>
    NotFound,

    /// <summary>The server refused the request for a reason of its own (another 4xx).</summary>
    Rejected,

    /// <summary>The server failed (5xx).</summary>
    Server,

    /// <summary>The server could not be reached, or did not answer in time.</summary>
    Network,

    /// <summary>Something answered, but not the way a ComicMaintainer server does.</summary>
    InvalidResponse,

    /// <summary>The server address the user entered cannot be used.</summary>
    InvalidAddress,

    /// <summary>The server has no accounts yet, so nobody can sign in until it is set up in a browser.</summary>
    SetupRequired,

    /// <summary>The device could not store the credential securely.</summary>
    CredentialStorage,
}

/// <summary>A request that failed. <see cref="Exception.Message"/> is written for the user and safe to show.</summary>
public sealed class ApiException : Exception
{
    public ApiException(ApiErrorKind kind, string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public ApiErrorKind Kind { get; }

    /// <summary>The HTTP status the server answered with, when it answered at all.</summary>
    public HttpStatusCode? StatusCode { get; }
}
