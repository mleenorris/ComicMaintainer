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
    private const string AggregatePrefix = "External metadata providers unavailable: ";

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

    /// <summary>
    /// What to tell the person using the app, built only from the provider names,
    /// <see cref="Kind"/> and <see cref="RetryAfter"/>. <see cref="Exception.Message"/> belongs
    /// in the log: it can carry the text of an inner exception or the provider's own error
    /// wording, such as a host and port or a JSON parser's complaint.
    /// </summary>
    public string UserMessage => Failures.Count > 0
        ? AggregatePrefix + string.Join("; ", Failures.Select(failure => failure.UserMessage))
        : Kind switch
        {
            ExternalMetadataFailureKind.Timeout => $"{Providers[0]} did not answer in time",
            ExternalMetadataFailureKind.RateLimited when RetryAfter is { TotalSeconds: > 0 } wait =>
                $"{Providers[0]} is limiting requests; try again in {(int)Math.Ceiling(wait.TotalSeconds)}s",
            ExternalMetadataFailureKind.RateLimited => $"{Providers[0]} is limiting requests; try again later",
            ExternalMetadataFailureKind.InvalidResponse => $"{Providers[0]} returned a response that could not be read",
            ExternalMetadataFailureKind.Configuration => $"{Providers[0]} rejected the request; check its settings",
            ExternalMetadataFailureKind.Paced => $"{Providers[0]} is at its request pace limit; try again shortly",
            _ => $"{Providers[0]} could not be reached or answered with an error",
        };

    private static string BuildMessage(IReadOnlyCollection<ExternalMetadataUnavailableException> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            throw new ArgumentException("At least one provider failure is required.", nameof(failures));
        }

        return AggregatePrefix + string.Join("; ", failures.Select(failure => failure.Message));
    }
}
