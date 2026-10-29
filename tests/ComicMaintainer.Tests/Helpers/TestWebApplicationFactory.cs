using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> for <c>Program</c> that applies the given host
/// configuration. Use it instead of <c>new WebApplicationFactory&lt;Program&gt;().WithWebHostBuilder(...)</c>:
/// the factory <c>WithWebHostBuilder</c> returns is a child of the one it is called on, and disposing the
/// child leaves that parent undisposed.
/// </summary>
internal sealed class TestWebApplicationFactory(Action<IWebHostBuilder> configureWebHost) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => configureWebHost(builder);
}
