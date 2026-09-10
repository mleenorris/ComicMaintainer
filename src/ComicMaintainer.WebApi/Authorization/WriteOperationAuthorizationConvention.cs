using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace ComicMaintainer.WebApi.Authorization;

/// <summary>
/// Applies an authorization policy to every state-changing (non-safe HTTP
/// method) action on the controllers that own the comic library and the
/// application configuration.
/// </summary>
/// <remarks>
/// A convention is used instead of per-action attributes so that endpoints
/// added later are protected by default — with ~30 write endpoints on
/// <c>FilesController</c> alone it is otherwise easy to forget one. Read
/// (<c>GET</c>/<c>HEAD</c>/<c>OPTIONS</c>) actions are intentionally left
/// untouched so <c>ReadOnly</c> users can still browse and read the library,
/// and controllers that only touch per-user state (reader progress,
/// preferences, account management) are not covered at all.
/// </remarks>
public class WriteOperationAuthorizationConvention : IActionModelConvention
{
    private static readonly HashSet<string> SafeHttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "HEAD", "OPTIONS"
    };

    /// <summary>
    /// Controller name (without the <c>Controller</c> suffix) to the policy
    /// required for its write actions.
    /// </summary>
    private static readonly Dictionary<string, string> PolicyByController = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Files"] = AuthorizationPolicies.CanModifyLibrary,
        ["Jobs"] = AuthorizationPolicies.CanModifyLibrary,
        ["Process"] = AuthorizationPolicies.CanModifyLibrary,
        ["Metadata"] = AuthorizationPolicies.CanModifyLibrary,
        ["Status"] = AuthorizationPolicies.CanModifyLibrary,
        ["SeriesImages"] = AuthorizationPolicies.CanModifyLibrary,
        ["ScheduledJobs"] = AuthorizationPolicies.CanModifyLibrary,
        ["Watcher"] = AuthorizationPolicies.CanModifyLibrary,
        ["Settings"] = AuthorizationPolicies.CanAdminister
    };

    public void Apply(ActionModel action)
    {
        var controllerName = action.Controller.ControllerName;
        if (!PolicyByController.TryGetValue(controllerName, out var policy))
        {
            return;
        }

        // Never override an explicit opt-out or an explicit, stricter policy
        // already declared on the action (e.g. the Admin-only restart action).
        if (action.Attributes.OfType<IAllowAnonymous>().Any())
        {
            return;
        }

        if (action.Attributes.OfType<IAuthorizeData>().Any(a => !string.IsNullOrEmpty(a.Policy) || !string.IsNullOrEmpty(a.Roles)))
        {
            return;
        }

        foreach (var selector in action.Selectors)
        {
            var httpMethods = selector.ActionConstraints
                .OfType<HttpMethodActionConstraint>()
                .SelectMany(c => c.HttpMethods)
                .ToList();

            // An action with no HTTP method constraint responds to every verb,
            // so it must be treated as a write endpoint.
            if (httpMethods.Count > 0 && httpMethods.All(SafeHttpMethods.Contains))
            {
                continue;
            }

            selector.EndpointMetadata.Add(new AuthorizeAttribute(policy));
        }
    }
}
