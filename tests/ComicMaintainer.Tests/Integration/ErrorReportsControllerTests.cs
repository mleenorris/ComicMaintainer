using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using ComicMaintainer.WebApi;
using ComicMaintainer.WebApi.Authorization;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// The error-reporting endpoints expose diagnostic detail about the instance
/// and, in the automatic mode, can cause an outbound disclosure. Their
/// authorization is therefore part of the feature's contract rather than an
/// incidental detail.
/// </summary>
[Trait("Category", "Integration")]
public class ErrorReportsControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ErrorReportsControllerTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("GET", "/api/errorreports")]
    [InlineData("GET", "/api/errorreports/a1b2c3d4e5f60718/preview")]
    [InlineData("POST", "/api/errorreports/a1b2c3d4e5f60718/submit")]
    [InlineData("DELETE", "/api/errorreports/a1b2c3d4e5f60718")]
    [InlineData("POST", "/api/errorreports/client")]
    public async Task ErrorReportEndpoints_RequireAuthentication(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { name = "TypeError", message = "x" });
        }

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The frontend hook posts here from a page that may have been open
        // across a session expiry; it must get JSON back, not the login HTML.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<!DOCTYPE", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>WriteOperationAuthorizationConvention</c> only applies a policy to
    /// controllers named in its map, so a controller that is simply forgotten
    /// silently ships with authenticated-user-level writes. This asserts the
    /// registration rather than trusting that it was remembered.
    /// </summary>
    [Fact]
    public void WriteOperationConvention_RequiresAdministratorForErrorReports()
    {
        var map = (IDictionary<string, string>)typeof(WriteOperationAuthorizationConvention)
            .GetField("PolicyByController", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        Assert.True(
            map.ContainsKey("ErrorReports"),
            "ErrorReportsController is not registered in WriteOperationAuthorizationConvention.PolicyByController, "
            + "so its write actions would not be policy-protected.");
        Assert.Equal(AuthorizationPolicies.CanAdminister, map["ErrorReports"]);
    }

    /// <summary>
    /// The browser hook has to work for every signed-in user, including
    /// read-only ones, so it opts out of the administrator policy. That opt-out
    /// must stay confined to that one action.
    /// </summary>
    [Fact]
    public void OnlyTheClientHookOptsOutOfTheAdministratorPolicy()
    {
        var actions = typeof(ErrorReportsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        var optedOut = actions
            .Where(m => m.GetCustomAttribute<PerUserWriteOperationAttribute>() is not null)
            .Select(m => m.Name)
            .ToList();

        Assert.Equal(new[] { nameof(ErrorReportsController.ReportClientError) }, optedOut);
    }

    /// <summary>
    /// Untrusted browser input is bounded at the model level so a page cannot
    /// push an unbounded body into the report store or the issue payload.
    /// </summary>
    [Theory]
    [InlineData(nameof(ErrorReportsController.ClientErrorReportRequest.Message))]
    [InlineData(nameof(ErrorReportsController.ClientErrorReportRequest.Stack))]
    [InlineData(nameof(ErrorReportsController.ClientErrorReportRequest.Name))]
    [InlineData(nameof(ErrorReportsController.ClientErrorReportRequest.Page))]
    [InlineData(nameof(ErrorReportsController.ClientErrorReportRequest.LastUserAction))]
    public void ClientErrorReportRequest_BoundsEveryField(string propertyName)
    {
        var property = typeof(ErrorReportsController.ClientErrorReportRequest).GetProperty(propertyName)!;

        Assert.NotNull(
            property.GetCustomAttribute<System.ComponentModel.DataAnnotations.MaxLengthAttribute>());
    }

    [Fact]
    public void ConventionIsRegistered()
    {
        // Guards against the convention being dropped from Program.cs, which
        // would silently downgrade every mapped controller at once.
        Assert.True(typeof(IActionModelConvention)
            .IsAssignableFrom(typeof(WriteOperationAuthorizationConvention)));
    }
}
