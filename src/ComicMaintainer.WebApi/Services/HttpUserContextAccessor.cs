using System.Security.Claims;
using ComicMaintainer.Core.Interfaces;

namespace ComicMaintainer.WebApi.Services;

/// <summary>
/// Resolves the current user from the ambient HTTP request.
/// </summary>
/// <remarks>
/// Registered as a singleton so it can be injected into singleton services such as
/// <c>FileStoreService</c>. This is safe because <see cref="IHttpContextAccessor"/> reads
/// the context from an <c>AsyncLocal</c>, so the value is still per-request. Outside a
/// request — hosted services, scheduled jobs, startup — there is no context and
/// <see cref="UserId"/> is <c>null</c>.
/// </remarks>
public sealed class HttpUserContextAccessor : IUserContextAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpUserContextAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string? UserId
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return string.IsNullOrEmpty(userId) ? null : userId;
        }
    }
}
