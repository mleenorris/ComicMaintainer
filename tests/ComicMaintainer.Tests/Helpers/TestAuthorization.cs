using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace ComicMaintainer.Tests.Helpers;

/// <summary>Authorization services that give the same answer to every policy.</summary>
internal static class TestAuthorization
{
    /// <summary>Every policy succeeds, as for an administrator.</summary>
    public static IAuthorizationService Administrator { get; } = new Fixed(AuthorizationResult.Success());

    /// <summary>Every policy fails, as for a signed-in user who is not an administrator.</summary>
    public static IAuthorizationService NotAdministrator { get; } = new Fixed(AuthorizationResult.Failed());

    private sealed class Fixed(AuthorizationResult result) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(result);

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(result);
    }
}
