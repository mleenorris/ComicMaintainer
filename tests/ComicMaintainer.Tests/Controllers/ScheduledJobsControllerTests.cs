using System.Reflection;
using ComicMaintainer.WebApi.Authorization;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace ComicMaintainer.Tests.Controllers;

public class ScheduledJobsControllerTests
{
    [Fact]
    public void Controller_IsAdminOnlyIncludingReads()
    {
        // The UI only offers the Scheduled Jobs page to administrators; the
        // API must enforce the same rule rather than rely on a hidden link.
        var attribute = typeof(ScheduledJobsController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(AuthorizationPolicies.CanAdminister, attribute!.Policy);
        Assert.Null(typeof(ScheduledJobsController).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.DoesNotContain(
            typeof(ScheduledJobsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null
                || (m.GetCustomAttribute<AuthorizeAttribute>() is { } a && a.Policy != AuthorizationPolicies.CanAdminister));
    }
}
