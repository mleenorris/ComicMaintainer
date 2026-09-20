using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// Groups every test class that boots the real application through
/// <see cref="WebApplicationFactory{TEntryPoint}"/> into a single xUnit collection.
/// </summary>
/// <remarks>
/// <para>
/// These classes start the app with its real configuration, which opens the configured SQLite
/// database. <c>xunit.runner.json</c> sets <c>parallelizeTestCollections: true</c> with unlimited
/// threads, so as separate collections they booted concurrently and contended on the same database
/// file — producing intermittent <c>SqliteException</c> failures during startup writes.
/// </para>
/// <para>
/// Sharing one collection serialises them and reuses a single host across all of them, which also
/// removes several redundant application startups from the run.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public class WebApplicationCollection : ICollectionFixture<WebApplicationFactory<Program>>
{
    public const string Name = "WebApplication";
}
