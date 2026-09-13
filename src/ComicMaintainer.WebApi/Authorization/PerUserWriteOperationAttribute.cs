namespace ComicMaintainer.WebApi.Authorization;

/// <summary>
/// Marks a state-changing action as touching only the calling user's own state, exempting
/// it from <see cref="WriteOperationAuthorizationConvention"/>.
/// </summary>
/// <remarks>
/// The convention treats every non-safe HTTP method on a library controller as a library
/// mutation requiring <c>CanModifyLibrary</c>. A few actions on those controllers write
/// per-user state instead — marking an issue read, for example, which since read status
/// became per-user no longer changes anything another user can observe. Those must stay
/// available to <c>ReadOnly</c> users, who are explicitly allowed to read the library.
/// The action still requires an authenticated user via the controller's <c>[Authorize]</c>;
/// this attribute only suppresses the additional policy.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class PerUserWriteOperationAttribute : Attribute
{
}
