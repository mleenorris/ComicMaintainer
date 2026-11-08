namespace ComicMaintainer.Core.Models;

/// <summary>Why an external metadata provider could not answer a request.</summary>
public enum ExternalMetadataFailureKind
{
    /// <summary>
    /// The provider could not be reached or answered with a server error
    /// (DNS or connection failure, HTTP 5xx or another unexpected status).
    /// </summary>
    Unavailable,

    /// <summary>The request did not complete within the provider timeout.</summary>
    Timeout,

    /// <summary>
    /// The provider throttled the request (HTTP 429/420), or the client-side
    /// request budget for the provider is used up.
    /// </summary>
    RateLimited,

    /// <summary>
    /// The provider answered with a body that could not be read: malformed
    /// JSON, or an API error envelope instead of results.
    /// </summary>
    InvalidResponse,

    /// <summary>
    /// The provider rejected the request because of the configuration, for
    /// example an invalid API key (HTTP 401/403).
    /// </summary>
    Configuration,

    /// <summary>
    /// The application's own request pacing for the provider had no room
    /// within the time the caller could wait, so nothing was sent. This says
    /// nothing about the provider: it is neither backed off from nor reported
    /// as unhealthy, and the lookup can simply be tried again.
    /// </summary>
    Paced
}

/// <summary>
/// Thrown when an external metadata provider failed to answer, as opposed to
/// answering that it has no matching series. Callers must not treat it as
/// "not found": nothing is known about the series, so an existing match must
/// be kept and the lookup retried later.
/// </summary>
/// <remarks>
/// A single provider throws it with one entry in <see cref="Providers"/>. The
/// composite provider throws an aggregate (built with
/// <see cref="ExternalMetadataUnavailableException(IReadOnlyCollection{ExternalMetadataUnavailableException})"/>)
/// when no provider produced a match and at least one of them failed or was
/// skipped while backing off from an earlier failure.
/// </remarks>
public sealed class ExternalMetadataUnavailableException : Exception
{
    /// <summary>Creates the failure of a single provider.</summary>
    public ExternalMetadataUnavailableException(
        string provider,
        ExternalMetadataFailureKind kind,
        string message,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Providers = new[] { provider };
        Kind = kind;
        RetryAfter = retryAfter;
        Failures = Array.Empty<ExternalMetadataUnavailableException>();
    }

    /// <summary>
    /// Combines the failures of several providers into one. <see cref="Kind"/>
    /// is the kind shared by every failure, or
    /// <see cref="ExternalMetadataFailureKind.Unavailable"/> when they differ.
    /// </summary>
    public ExternalMetadataUnavailableException(IReadOnlyCollection<ExternalMetadataUnavailableException> failures)
        : base(BuildMessage(failures), failures.Count == 1 ? failures.First() : new AggregateException(failures))
    {
        Failures = failures.ToList();
        Providers = failures.SelectMany(failure => failure.Providers).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var kinds = failures.Select(failure => failure.Kind).Distinct().ToList();
        Kind = kinds.Count == 1 ? kinds[0] : ExternalMetadataFailureKind.Unavailable;
        RetryAfter = failures.Max(failure => failure.RetryAfter);
    }

    /// <summary>Names of the providers that failed.</summary>
    public IReadOnlyList<string> Providers { get; }

    /// <summary>What went wrong.</summary>
    public ExternalMetadataFailureKind Kind { get; }

    /// <summary>
    /// How long the provider asked the client to wait before trying again,
    /// when it said so (HTTP <c>Retry-After</c>, or the time until the
    /// client-side budget refills).
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>The per-provider failures of an aggregate; empty for a single provider.</summary>
    public IReadOnlyList<ExternalMetadataUnavailableException> Failures { get; }

    private static string BuildMessage(IReadOnlyCollection<ExternalMetadataUnavailableException> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            throw new ArgumentException("At least one provider failure is required.", nameof(failures));
        }

        return "External metadata providers unavailable: "
            + string.Join("; ", failures.Select(failure => failure.Message));
    }
}
