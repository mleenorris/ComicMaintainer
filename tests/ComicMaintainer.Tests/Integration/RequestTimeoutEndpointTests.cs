using System.Net;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Request timeouts are configured once and applied everywhere, which makes
/// their failure mode quiet and expensive: the default policy caps every
/// request, so an endpoint that legitimately runs for minutes — condensing a
/// whole series into one book, then streaming a multi-gigabyte result — was
/// being cut off at thirty seconds with an empty 504. These tests hold the
/// endpoints that must be exempt, and the wiring that lets them be.
/// </summary>
public class RequestTimeoutEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RequestTimeoutEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Theory]
    // Builds the book inside the request, then streams it back.
    [InlineData("api/Email/condense-download")]
    // Streams a finished book that can run to gigabytes.
    [InlineData("api/Email/condense-builds/{buildId:guid}/download")]
    // Open for as long as the page is.
    [InlineData("api/Events/stream")]
    public void UnboundedEndpointsOptOutOfTheRequestTimeout(string routePattern)
    {
        var endpoint = FindEndpoint(routePattern);

        Assert.NotNull(endpoint.Metadata.GetMetadata<DisableRequestTimeoutAttribute>());
    }

    [Theory]
    // Resolves and plans across a series of up to a thousand issues.
    [InlineData("api/Email/condense-plan")]
    [InlineData("api/Email/condense-builds")]
    // Queueing walks and stats every file in the request.
    [InlineData("api/Email/send")]
    [InlineData("api/Email/send-series")]
    public void BulkEndpointsRunUnderTheLongRunningPolicy(string routePattern)
    {
        var endpoint = FindEndpoint(routePattern);
        var timeout = endpoint.Metadata.GetMetadata<RequestTimeoutAttribute>();

        Assert.NotNull(timeout);
        Assert.Equal(RequestTimeoutPolicies.LongRunning, timeout!.PolicyName);
    }

    [Fact]
    public void TheLongRunningPolicyIsActuallyRegistered()
    {
        // An endpoint naming a policy that was never added throws at request
        // time, so the attributes above are only safe while this holds.
        var options = _factory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<RequestTimeoutOptions>>()
            .Value;

        Assert.True(options.Policies.ContainsKey(RequestTimeoutPolicies.LongRunning));
        Assert.NotNull(options.DefaultPolicy);
        // Both policies must answer a timeout themselves; the framework's own
        // response is an empty body and a warning nobody reports.
        Assert.NotNull(options.DefaultPolicy!.WriteTimeoutResponse);
        Assert.NotNull(options.Policies[RequestTimeoutPolicies.LongRunning].WriteTimeoutResponse);
    }

    [Fact]
    public void TheProgressHubIsNotSubjectToTheDefaultTimeout()
    {
        var hub = _factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .FirstOrDefault(e => e.DisplayName?.Contains("/hubs/progress", StringComparison.Ordinal) == true);

        Assert.NotNull(hub);
        Assert.NotNull(hub!.Metadata.GetMetadata<DisableRequestTimeoutAttribute>());
    }

    [Fact]
    public async Task TimeoutMiddlewareHonorsRoutedEndpointMetadata()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<RequestTimeoutOptions>(options =>
                    options.DefaultPolicy = RequestTimeoutPolicies.Create(TimeSpan.FromMilliseconds(50)));
                services.Configure<MvcOptions>(options => options.Filters.Add(new SlowActionFilter()));
                services.AddSingleton<IAuthorizationMiddlewareResultHandler, TestAuthorizationHandler>();
            });
        });
        using var client = factory.CreateClient();

        var normal = await client.GetAsync("/api/version");
        Assert.Equal(HttpStatusCode.GatewayTimeout, normal.StatusCode);

        var optedOut = await client.GetAsync("/api/events/stream");
        Assert.Equal(HttpStatusCode.OK, optedOut.StatusCode);
    }

    private sealed class SlowActionFilter : IAsyncActionFilter
    {
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            await Task.Delay(200, context.HttpContext.RequestAborted);
            context.Result = new OkResult();
        }
    }

    private sealed class TestAuthorizationHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(RequestDelegate next, HttpContext context,
            AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult) => next(context);
    }

    private RouteEndpoint FindEndpoint(string routePattern)
    {
        var endpoint = _factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .FirstOrDefault(e => string.Equals(
                e.RoutePattern.RawText,
                routePattern,
                StringComparison.OrdinalIgnoreCase));

        Assert.True(endpoint is not null, $"No endpoint is mapped to '{routePattern}'.");
        return endpoint!;
    }
}
