namespace ComicMaintainer.WebApi.Infrastructure;

/// <summary>
/// The sentence a client gets when this action fails with an unhandled exception, in place of
/// <see cref="GlobalExceptionHandler"/>'s generic text. The UI shows a failure response's
/// <c>error</c> field to the user, so an action that names what went wrong ("Error reading
/// page") stays more useful than "the request could not be completed", without each action
/// needing a catch block of its own.
/// </summary>
/// <remarks>
/// The message is a fixed string chosen by the action's author. It goes to every client, so
/// it must never be built from an exception or from request data.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FailureMessageAttribute(string message) : Attribute
{
    /// <summary>What failed, in a form safe to show to any client.</summary>
    public string Message { get; } = message;
}
