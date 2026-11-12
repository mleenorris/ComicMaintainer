using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// The application's container is built with its registrations validated (the test host runs
/// in Development, where the host validates the graph), so a constructor cycle or a missing
/// registration fails here instead of silently stopping a feature in production. The services
/// below are the ones that used to be reached through runtime lookups.
/// </summary>
[Trait("Category", "Integration")]
public class ServiceGraphTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ServiceGraphTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public void TestHost_RunsInDevelopment_SoTheContainerIsValidatedOnBuild()
    {
        Assert.True(_factory.Services.GetRequiredService<IHostEnvironment>().IsDevelopment());
    }

    [Fact]
    public void Constructors_DoNotDeclareCollaboratorsAsOptional()
    {
        // An optional parameter hides a missing registration from the validated container and
        // silently replaces the collaborator with nothing, so only values and telemetry may
        // have a default (see docs/IOC_DESIGN_PRINCIPLES.md).
        var assemblies = new[] { typeof(ComicProcessorService).Assembly, typeof(Program).Assembly };

        var optionalCollaborators = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .SelectMany(type => type.GetConstructors())
            .SelectMany(constructor => constructor.GetParameters())
            .Where(parameter => parameter.HasDefaultValue && IsCollaboratorType(parameter.ParameterType))
            .Select(parameter => $"{parameter.Member.DeclaringType!.FullName}: {parameter.Name}")
            .ToList();

        Assert.Empty(optionalCollaborators);
    }

    [Fact]
    public void ScheduledJobRunner_IsTheHostedServiceThatArmsTheTimers()
    {
        var runner = _factory.Services.GetRequiredService<IScheduledJobRunner>();

        Assert.Same(_factory.Services.GetRequiredService<ScheduledJobsHostedService>(), runner);
    }

    [Fact]
    public void CoverAndEmailQueues_AreConsumedByHostedServices()
    {
        var hostedServices = _factory.Services.GetServices<IHostedService>().ToList();

        Assert.Single(hostedServices.OfType<SeriesCoverArtifactQueueHostedService>());
        Assert.Single(hostedServices.OfType<ComicEmailQueueHostedService>());
    }

    [Fact]
    public void Services_ThatHadRuntimeLookups_ResolveFromTheContainer()
    {
        var services = _factory.Services;

        Assert.NotNull(services.GetRequiredService<ISeriesCoverArtifactWriter>());
        Assert.NotNull(services.GetRequiredService<ISeriesFolderCoverWriter>());
        Assert.NotNull(services.GetRequiredService<ISeriesArchiveCoverWriter>());
        Assert.NotNull(services.GetRequiredService<ISeriesMetadataCacheService>());
        Assert.NotNull(services.GetRequiredService<IComicEmailQueue>());
        Assert.NotNull(services.GetRequiredService<IComicEmailService>());
    }

    private static bool IsCollaboratorType(Type type) =>
        type.IsInterface && type.Namespace?.StartsWith("ComicMaintainer.", StringComparison.Ordinal) == true;
}
