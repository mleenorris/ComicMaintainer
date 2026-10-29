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
    public void WriteActionOnProtectedController_GetsTheControllersPolicy(string controllerName, string expectedPolicy)
    {
        var action = CreateAction(controllerName, Array.Empty<object>(), ["POST"]);

        new WriteOperationAuthorizationConvention().Apply(action);

        Assert.Equal(new[] { expectedPolicy }, AppliedPolicies(Assert.Single(action.Selectors)));
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
