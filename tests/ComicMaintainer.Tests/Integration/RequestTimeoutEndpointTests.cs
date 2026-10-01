using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
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
