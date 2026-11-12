# Dependency Injection Principles

How ComicMaintainer wires its services, and the deliberate exceptions to those rules.

Last reviewed: 2026-11-08, for the 3.0.0 release.

## The rules

1. **Constructor injection.** Services and controllers receive their dependencies through their constructors. Registrations live in `src/ComicMaintainer.WebApi/Startup/`.
2. **Interfaces for services.** A consumer depends on an interface (`IFileStoreService`, `IScheduledJobRunner`), not on a concrete class, so a decorator or test double can stand in for it.
3. **Required dependencies are required.** A production service is declared without a default value. An optional parameter that silently replaces a missing registration with a no-op or a private instance turns a wiring mistake into missing behaviour.
4. **`IDbContextFactory` for database access from singletons.** Singletons create short-lived contexts with `IDbContextFactory<ComicMaintainerDbContext>`, not `IServiceProvider`.
5. **`IOptionsMonitor<T>` for configuration**, so changes in Settings apply without a restart.
6. **`ILogger<T>` for logging.** Service code does not use a static logger.

## Dependency cycles

There are none in the service graph. The container validates it at startup in Development, and `tests/ComicMaintainer.Tests/Integration/ServiceGraphTests.cs` runs against that validated container in CI.

Where one service needs to hand work to another that (indirectly) depends on it, the work goes through a queue with no dependencies of its own, and the consumer is given to the queue when it is started:

| Producer | Queue | Consumer, started by |
| --- | --- | --- |
| `SeriesMetadataCacheService` | `ISeriesCoverArtifactQueue` | `ISeriesCoverArtifactWriter`, `SeriesCoverArtifactQueueHostedService` |
| `ComicEmailService` | `IComicEmailQueue` | `IComicEmailService`, `ComicEmailQueueHostedService` |

`SeriesMetadataCacheService` depends on neither cover writer: the writers depend on `ISeriesLibraryService`, which depends on the cache. A cover change is a queue item, and re-applying a cover on request goes through `ISeriesCoverArtifactWriter`, which sits above the cache.

Do not break a new cycle with a `Func<T>`, `Lazy<T>` or `IServiceScopeFactory` lookup. Those hide the dependency from the container, so a wrong registration only shows up when the code path runs.

## Locks shared between services

Exclusion between services goes through injected singletons, not static fields:

| Service | Guards |
| --- | --- |
| `IFileOperationLock` | One archive at a time, across the watcher, batch jobs, API metadata edits and the series cover writer |
| `ILibraryMutationLock` | Creating and removing library folders, across the Suwayomi importer and folder combine |

Folder combine itself lives in the Core service `IFolderCombineService`, so its controller action stays thin.

## Deliberate exceptions

Service resolution at runtime remains in places that the framework constructs or calls outside dependency injection. Each resolves only framework-owned or request-scoped services:

| Location | Why |
| --- | --- |
| `Authentication/JwtSessionEvents` | JWT bearer events are options objects created before the container exists; the validator comes from the request. |
| `Infrastructure/LibraryETagAttribute` | Attribute filters are created by MVC. The attribute reads its services from the request and skips the ETag when they are absent. |
| `Infrastructure/RateLimitRejection`, `Infrastructure/RequestTimeoutPolicies` | Rate-limit and timeout callbacks are registered as delegates. |
| `Controllers/DiagnosticsController` | Reads the hosting environment from `HttpContext.RequestServices` for one optional field of the report. |
| `Program.cs`, `Startup/WebApplicationExtensions`, `Startup/TelemetryExtensions`, `Startup/WebServiceCollectionExtensions` | The composition root. Startup steps create a scope to migrate the database and seed roles. |
| `Startup/CoreServiceCollectionExtensions` | Registration factories (`sp => ...`), for example the database context factory. |
| `MauiApp/Views/MainPage.xaml.cs` | The Android app's page resolves the pages it navigates to. |

Static state is limited to stateless helpers (`LoggingHelper`, `LibraryPathValidator`, `ComicFileExtensions`, ...), constants, and these process-wide cases:

| Location | Why |
| --- | --- |
| `Services/ProviderRateLimiter.cs` (`SharedRateLimiters`) | AniList's limit is per IP across the manga and manhwa providers, so the two services must share one limiter. |
| `Services/AuthService` (`SetupLock`) | Serializes first-run setup, so two concurrent requests cannot both create the administrator. |
| `Authentication/AutheliaAuthenticationHandler` (`ReportedUntrustedPeers`) | Remembers which untrusted peers were already reported at Warning, bounded so a scan cannot grow it. The handler is created per request, so this cannot live on the instance. |

Serilog's static `Log` is used only for messages written before the container exists.

Optional constructor parameters remain only for values that are not collaborators, or that the service can do without at no cost to its behaviour:

- **`TimeProvider? timeProvider = null`**: defaults to `TimeProvider.System`; tests pass a fake clock.
- **`ComicMaintainerMetrics? metrics = null`**: telemetry a service can run without.
- **`IHostApplicationLifetime? applicationLifetime = null`**: only used to observe shutdown.
- **Timing and tuning values** (`TimeSpan? startupDelay`, `pollInterval`, `retryDelays`, `providerCallTimeout`, ...): default to the production value; tests shorten them.
- **`ProviderRateLimiter? rateLimiter = null`** on the metadata providers: defaults to the provider's production limiter; tests pass one with their own budget.

Every collaborator that production registers is a required parameter. `ServiceGraphTests.Constructors_DoNotDeclareCollaboratorsAsOptional` enforces this, and tests build these services through `tests/ComicMaintainer.Tests/Helpers/TestServices.cs`, which fills in inert collaborators for the ones a test does not care about.

## Review checklist

- [ ] New services are registered in `Startup/` and reached through an interface.
- [ ] No new `IServiceProvider`, `IServiceScopeFactory`, `Func<T>` or `Lazy<T>` injected to hide a dependency.
- [ ] No new optional constructor parameter for a collaborator production always has.
- [ ] Singletons use `IDbContextFactory` for database access.
- [ ] The integration tests still pass, so the application's service graph still validates.

## References

- [Microsoft: Dependency injection in .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection)
- [Microsoft: DbContext lifetime](https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/)
- [Service Locator is an Anti-Pattern](https://blog.ploeh.dk/2010/02/03/ServiceLocatorisanAnti-Pattern/)
