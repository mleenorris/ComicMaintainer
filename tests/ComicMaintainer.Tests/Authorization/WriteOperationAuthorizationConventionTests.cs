using System.Reflection;
using ComicMaintainer.WebApi.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace ComicMaintainer.Tests.Authorization;

public class WriteOperationAuthorizationConventionTests
{
    private static readonly MethodInfo ActionMethod = typeof(SampleController).GetMethod(nameof(SampleController.Act))!;

    private static ActionModel CreateAction(string controllerName, object[] attributes, params string[][] selectorMethods)
    {
        var controller = new ControllerModel(typeof(SampleController).GetTypeInfo(), Array.Empty<object>())
        {
            ControllerName = controllerName
        };
        var action = new ActionModel(ActionMethod, attributes) { Controller = controller, ActionName = "Act" };
        foreach (var methods in selectorMethods)
        {
            var selector = new SelectorModel();
            if (methods.Length > 0)
            {
                selector.ActionConstraints.Add(new HttpMethodActionConstraint(methods));
            }

            action.Selectors.Add(selector);
        }

        return action;
    }

    private static string?[] AppliedPolicies(SelectorModel selector) =>
        selector.EndpointMetadata.OfType<AuthorizeAttribute>().Select(a => a.Policy).ToArray();

    [Theory]
    [InlineData("Files", AuthorizationPolicies.CanModifyLibrary)]
    [InlineData("files", AuthorizationPolicies.CanModifyLibrary)]
    [InlineData("Watcher", AuthorizationPolicies.CanModifyLibrary)]
    [InlineData("Suwayomi", AuthorizationPolicies.CanModifyLibrary)]
    [InlineData("Settings", AuthorizationPolicies.CanAdminister)]
    [InlineData("ScheduledJobs", AuthorizationPolicies.CanAdminister)]
    [InlineData("Users", AuthorizationPolicies.CanAdminister)]
    [InlineData("ComicPages", AuthorizationPolicies.CanModifyLibrary)]
    [InlineData("Covers", AuthorizationPolicies.CanModifyLibrary)]
    [InlineData("Duplicates", AuthorizationPolicies.CanModifyLibrary)]
    public void WriteActionOnProtectedController_GetsTheControllersPolicy(string controllerName, string expectedPolicy)
    {
        var action = CreateAction(controllerName, Array.Empty<object>(), ["POST"]);

        new WriteOperationAuthorizationConvention().Apply(action);

        Assert.Equal(new[] { expectedPolicy }, AppliedPolicies(Assert.Single(action.Selectors)));
    }

    /// <summary>
    /// Controllers whose writes only touch the caller's own state, or that sign people in: open
    /// to every signed-in (or anonymous) user by design.
    /// </summary>
    private static readonly HashSet<string> PerUserControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Auth", // signing in and out, the caller's own password and API keys
        "ClientErrors", // a browser reporting its own errors
        "ComicReader", // the caller's reading progress and read state
        "Preferences", // the caller's own preferences
        "Reader", // the caller's reading progress, preferences and sessions (mobile app)
    };

    [Fact]
    public void EveryWriteAction_OfEveryController_NeedsAPolicy_UnlessItOnlyTouchesTheCallersOwnState()
    {
        // A controller left out of the convention's list let read-only users write: every write
        // action must end up with a policy, declare its own access, or belong to a per-user controller.
        var controllers = typeof(WriteOperationAuthorizationConvention).Assembly.GetTypes()
            .Where(type => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(type) && !type.IsAbstract);
        var unprotected = new List<string>();
        foreach (var controllerType in controllers)
        {
            var name = controllerType.Name.EndsWith("Controller", StringComparison.Ordinal) ? controllerType.Name[..^"Controller".Length] : controllerType.Name;
            var controllerAttributes = controllerType.GetCustomAttributes(inherit: true);
            var controllerDeclaresAccess = controllerAttributes.OfType<IAuthorizeData>().Any(a => !string.IsNullOrEmpty(a.Policy) || !string.IsNullOrEmpty(a.Roles));
            foreach (var method in controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var attributes = method.GetCustomAttributes(inherit: true);
                var writeMethods = attributes.OfType<Microsoft.AspNetCore.Mvc.Routing.IActionHttpMethodProvider>()
                    .SelectMany(provider => provider.HttpMethods)
                    .Where(verb => verb is not ("GET" or "HEAD" or "OPTIONS"))
                    .ToArray();
                if (writeMethods.Length == 0 || controllerDeclaresAccess || PerUserControllers.Contains(name))
                {
                    continue;
                }

                var action = CreateAction(name, attributes, writeMethods);
                new WriteOperationAuthorizationConvention().Apply(action);
                var declaresAccess = attributes.OfType<IAllowAnonymous>().Any()
                    || attributes.OfType<PerUserWriteOperationAttribute>().Any()
                    || attributes.OfType<IAuthorizeData>().Any(a => !string.IsNullOrEmpty(a.Policy) || !string.IsNullOrEmpty(a.Roles));
                if (!declaresAccess && AppliedPolicies(Assert.Single(action.Selectors)).Length == 0)
                {
                    unprotected.Add($"{name}.{method.Name}");
                }
            }
        }

        Assert.True(unprotected.Count == 0, "Write actions without a policy: " + string.Join(", ", unprotected));
    }

    [Fact]
    public void AllowAnonymousAction_IsLeftOpen()
    {
        var action = CreateAction("Files", [new AllowAnonymousAttribute()], ["POST"]);

        new WriteOperationAuthorizationConvention().Apply(action);

        Assert.Empty(Assert.Single(action.Selectors).EndpointMetadata);
    }

    [Fact]
    public void PerUserWriteAction_IsLeftOpen()
    {
        var action = CreateAction("Files", [new PerUserWriteOperationAttribute()], ["PUT"]);

        new WriteOperationAuthorizationConvention().Apply(action);

        Assert.Empty(Assert.Single(action.Selectors).EndpointMetadata);
    }

    [Theory]
    [InlineData("Admin", null)]
    [InlineData(null, "Admin")]
    public void ExplicitPolicyOrRoles_AreNotOverridden(string? policy, string? roles)
    {
        var action = CreateAction("Settings", [new AuthorizeAttribute { Policy = policy, Roles = roles }], ["DELETE"]);

        new WriteOperationAuthorizationConvention().Apply(action);

        Assert.Empty(Assert.Single(action.Selectors).EndpointMetadata);
    }

    [Fact]
    public void BareAuthorizeAttribute_StillGetsTheWritePolicy()
    {
        var action = CreateAction("Jobs", [new AuthorizeAttribute()], ["POST"]);

        new WriteOperationAuthorizationConvention().Apply(action);

        Assert.Equal(new[] { AuthorizationPolicies.CanModifyLibrary }, AppliedPolicies(Assert.Single(action.Selectors)));
    }

    [Fact]
    public void UnknownController_IsLeftAlone()
    {
        var action = CreateAction("Reader", Array.Empty<object>(), ["POST"]);

        new WriteOperationAuthorizationConvention().Apply(action);

        Assert.Empty(Assert.Single(action.Selectors).EndpointMetadata);
    }

    [Fact]
    public void OnlyNonSafeSelectors_AreProtected()
    {
        var action = CreateAction(
            "Metadata",
            Array.Empty<object>(),
            ["GET"],
            ["head", "OPTIONS"],
            ["GET", "POST"],
            []);

        new WriteOperationAuthorizationConvention().Apply(action);

        Assert.Empty(action.Selectors[0].EndpointMetadata);
        Assert.Empty(action.Selectors[1].EndpointMetadata);
        Assert.Equal(new[] { AuthorizationPolicies.CanModifyLibrary }, AppliedPolicies(action.Selectors[2]));
        // No HTTP method constraint means the action answers every verb.
        Assert.Equal(new[] { AuthorizationPolicies.CanModifyLibrary }, AppliedPolicies(action.Selectors[3]));
    }

    private sealed class SampleController
    {
        public void Act()
        {
        }
    }
}
