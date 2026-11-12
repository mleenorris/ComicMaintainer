namespace ComicMaintainer.Core.Configuration;

/// <summary>
/// A settings value was refused by <c>SettingsService</c>; nothing was saved. Controllers turn
/// this one exception, and not an arbitrary <see cref="ArgumentException"/> from a bug, into a 400.
/// </summary>
public sealed class SettingsValidationException : ArgumentException
{
    public SettingsValidationException(string reason, string? paramName = null)
        : base(reason, paramName)
    {
        Reason = reason;
    }

    /// <summary>Why the value was refused, without the parameter name <see cref="ArgumentException.Message"/> appends.</summary>
    public string Reason { get; }
}
