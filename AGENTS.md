# Agents Guide for Recall Codebase

## Architecture Overview

**Recall** is an ASP.NET Core 10 web application that integrates with TheTVDB API to search and track TV series. It uses:
- **Razor Pages** for the UI layer
- **Entity Framework Core** with PostgreSQL for data persistence
- **Dependency Injection** for service management
- **Serilog** for structured logging
- **NUnit** with Moq for testing
- **Redis** (`StackExchange.Redis`) for distributed caching and token state
- **Repository pattern** for all data access (repositories return domain models, not entities)

The application follows a **layered architecture**:
- **Pages** (Razor Pages): Handle HTTP requests/responses
- **Services**: Business logic and orchestration
- **Infrastructure**: Data access (EF Core), external API clients, caching
- **Domain**: Models and DTOs
- **Mappings**: Static extension methods converting between DTOs, entities, and domain models

## Key Architectural Patterns

### 1. Service Registration via Extension Methods
Services are registered in `Recall.Web/Extensions/ServiceCollectionExtensions.cs` using extension methods (e.g., `AddTheTvDb()`, `AddApplicationServices()`). This pattern is called in `Program.cs` and centralizes configuration for feature areas.

**Pattern**: When adding new service integrations:
```csharp
// Extensions/ServiceCollectionExtensions.cs
public static IServiceCollection AddMyFeature(this IServiceCollection services, IConfiguration configuration) {
    services.Configure<MyOptions>(configuration.GetSection(MyOptions.SectionName));
    services.AddHttpClient<IMyClient, MyClient>(...);
    services.AddScoped<IMyService, MyService>();
    return services;
}

// Program.cs
builder.Services.AddMyFeature(builder.Configuration);
```

### 2. External API Integration (TheTVDB)
Located in `Recall.Web/Services/External/TheTvDb/`:
- **ITheTvDbApiClient** / **TheTvDbApiClient**: Low-level HTTP client
- **TheTvDbClientState**: **Singleton** — holds cached Bearer token + `SemaphoreSlim` request throttle (max 5 concurrent TVDB requests). Must be registered as Singleton so the token survives across transient typed-client instances.
- DTOs live in `Recall.Web/Infrastructure/External/TheTvDb/Dto/`
- **ITheTvDbService** (in `Recall.Web/Services/`): High-level service with business logic

The API client uses HttpClientFactory. The service layer handles filtering (e.g., excludes non-series types) and maps DTOs to domain models via `Recall.Web/Mappings/`.

Current `ITheTvDbService` methods:
- `SearchSeriesAsync(string query, CancellationToken)`
- `GetSeriesByIdAsync(int seriesId, CancellationToken)` → `TvSeriesDetails?`
- `GetSeriesAggregateByIdAsync(int seriesId, CancellationToken)` → `SeriesAggregate?`
- `GetEpisodeDetailsAsync(int episodeId, CancellationToken)` → `Episode?`
- `GetSeriesByIdExtendedAsync(int seriesId, CancellationToken)` → `Series?`

### 3. Database Layer Pattern
**AppDbContext** (`Recall.Web/Infrastructure/Persistence/AppDbContext.cs`):
- Uses EF Core 10 with PostgreSQL (Npgsql provider)
- **Split Query behavior** enabled to prevent Cartesian explosion
- **Automatic audit timestamps**: `CreatedUtc` and `UpdatedUtc` are set automatically via `ApplyAuditTimestamps()` override
- Entity configurations use `IEntityTypeConfiguration<T>` pattern via `ApplyConfigurationsFromAssembly()`
- DbSets: `AppUsers`, `TrackedSeries`, `EpisodeWatches`

**Entities** (`Infrastructure/Persistence/Entities/`):
- `AppUserEntity` — app-managed user: `Id` (Guid PK, also the `NameIdentifier` claim), `Username`, `Email`, audit timestamps. Passwordless — no password or external-id column.
- `TrackedSeriesEntity` — links a user to a TVDB series; has `uint Version` for PostgreSQL xmin-backed optimistic concurrency
- `EpisodeWatchEntity` — records that a user watched a specific episode

**Factory Pattern**: `AppDbContextFactory` implements `IDesignTimeDbContextFactory<AppDbContext>` for migrations:
```bash
dotnet ef migrations add MigrationName --project Recall.Web
dotnet ef database update --project Recall.Web
```

### 4. Razor Pages Handler Model
Pages follow a simple async handler pattern:
```csharp
// Pages/Series/Search.cshtml.cs
public sealed class SearchModel(ITheTvDbService service, ILogger<SearchModel> logger) : PageModel {
    [BindProperty(SupportsGet = true)]
    public string? Query { get; set; }
    
    public async Task OnGetAsync(CancellationToken cancellationToken) {
        // Handler logic with dependency injection via constructor
    }
}
```

**Key patterns**:
- Constructor-based dependency injection
- `[BindProperty(SupportsGet = true)]` for query parameters
- `[FromRoute]` for route-segment parameters (see `Pages/Episodes/Details.cshtml.cs`)
- `CancellationToken` is passed throughout async chains
- Error handling with typed exceptions (e.g., `TheTvDbApiException`)

**Current pages**:
- `Pages/Index` — home
- `Pages/Series/Search` — TVDB search
- `Pages/Series/Details` — series detail with tracking actions
- `Pages/Series/Library` — logged-in user's tracked series list
- `Pages/Episodes/Details` — episode detail with watched toggle / bulk mark-through
- `Pages/Account/Login`, `Pages/Account/Logout` — authentication stubs

### 5. Repository Pattern
Repositories (`Infrastructure/Persistence/Repositories/`) accept and return **domain models**, not EF entities. Mapping between entity ↔ domain happens inside the repository implementation.

```csharp
// Interface uses domain model
Task<IReadOnlyList<TrackedSeries>> GetByUserAsync(Guid userId, CancellationToken ct);

// Implementation maps internally using Mappings/ extension methods
return await dbContext.TrackedSeries
    .Where(e => e.UserId == userId)
    .Select(e => e.ToDomain())
    .ToListAsync(ct);
```

Interfaces:
- `IAppUserRepository` — `GetOrCreateByExternalIdAsync(externalId, email, displayName)`
- `ITrackedSeriesRepository` — CRUD for tracked series per user
- `IEpisodeWatchRepository` — mark/unmark watched, bulk range mark, prior-unwatched count

### 6. Mapping Layer
`Recall.Web/Mappings/` contains **static extension method classes** — no AutoMapper or reflection-based mapper.

Pattern:
```csharp
entity.ToDomain()   // TrackedSeriesEntity → TrackedSeries
domain.ToEntity()   // TrackedSeries → TrackedSeriesEntity
TrackedSeriesMappings.FromTvDbDetails(userId, details)  // TvSeriesDetails → TrackedSeries
dto.ToDomain()      // SeriesDataDto → Series (via SeriesMapping)
```

Files: `SeriesMapping.cs`, `EpisodeMappings.cs`, `SeasonMappings.cs`, `SeasonTypeMappings.cs`, `SeriesDataDtoMappings.cs`, `TrackedSeriesMappings.cs`, `UserMappings.cs`

### 7. Authentication & DevAuthMiddleware
`Middleware/DevAuthMiddleware.cs` (registered via `app.UseMiddleware<DevAuthMiddleware>()`) auto-injects a fixed dev user in `#if DEBUG` builds:
- Fixed user GUID: `11111111-1111-1111-1111-111111111111`, username `dev-user`
- Creates the user in the DB on first request if absent
- Injects `ClaimTypes.NameIdentifier` (as `Guid` string), `ClaimTypes.Name`, `ClaimTypes.Email`

**Do NOT add authentication gates that break this middleware** in DEBUG mode.

`ICurrentUserService` / `CurrentUserService` (`Services/`): thin wrapper over `IHttpContextAccessor` that exposes `IsAuthenticated`, `UserId` (parsed `Guid`), `ExternalUserId`, `Email`, `DisplayName` from claims.

### 8. Toast Notifications
`Extensions/PageModelToastExtensions.cs` uses **C# 14 `extension` block syntax** (not the older `this T` extension method syntax):

```csharp
// Usage in page models:
this.SetSuccessToast("Episode marked as watched.");
this.SetErrorToast("Could not load your library right now.");
this.SetInfoToast("Episode marked as not watched.");
```

TempData keys: `Toast.Success`, `Toast.Error`, `Toast.Info`. The Razor layout/partial reads these to render toast UI.

### 9. Caching Layer
`Infrastructure/Caching/IDistributedCacheJson` / `DistributedCacheJson`: Singleton wrapper over `IDistributedCache` (backed by Redis) that serializes/deserializes values as JSON with `JsonSerializerDefaults.Web`.

```csharp
await cache.GetAsync<T>(key, ct);
await cache.SetAsync(key, value, ttl, ct);
await cache.RemoveAsync(key, ct);
```

Also registered: `StackExchange.Redis.IConnectionMultiplexer` as Singleton (for distributed locking, separate from `IDistributedCache`).

## Testing Strategy

**Test Framework**: NUnit with Moq for mocking, AwesomeAssertions for fluent assertions

**Pattern** (example from `Recall.Tests/Services/TheTvDbServiceTests.cs`):
```csharp
[TestFixture]
public class TheTvDbServiceTests {
    [Test]
    public async Task SearchSeriesAsync_Should_FilterOut_NonSeriesTypes() {
        // Arrange: Set up mocks
        var mock = new Mock<ITheTvDbApiClient>();
        mock.Setup(x => x.SearchSeriesAsync(...))
            .ReturnsAsync(new List<...>());
        
        // Act: Execute service
        var sut = new TheTvDbService(mock.Object);
        var result = await sut.SearchSeriesAsync("query");
        
        // Assert: Use fluent assertions
        result.Should().HaveCount(2);
    }
}
```

**Run tests with coverage**:
```bash
cd Recall.Tests
./run-tests-with-coverage.sh  # Or: dotnet test --settings coverlet.runsettings
```

Coverage report generates HTML in `coverage-report/index.html`.

## Development Workflow

### Initial Setup
```bash
# Initialize user secrets for API credentials
dotnet user-secrets init --project ./Recall.Web

# Set TheTVDB API credentials (obtain from thetvdb.com)
dotnet user-secrets set "TheTvDb:ApiKey" "YOUR_API_KEY" --project ./Recall.Web
dotnet user-secrets set "TheTvDb:Pin" "YOUR_PIN" --project ./Recall.Web

# Start Redis (required — app will fail without it)
docker run --name my-redis -p 6379:6379 -d redis:7
```

### Run Application
```bash
# Watch mode with live reload
dotnet watch run --project Recall.Web --launch-profile Recall.Web
```

### Build & Publish
```bash
# Build Docker image
docker build -f Recall.Web/Dockerfile -t recall.web .

# Compose (simple config, see compose.yaml)
docker-compose up
```

### Database Operations
Requires `ASPNETCORE_ENVIRONMENT` and connection string setup. The `AppDbContextFactory` reads from `appsettings.{Environment}.json`.

## Configuration & Secrets

**appsettings.json** structure:
- `Logging`: Default log levels
- `TheTvDb`: BaseUrl, ApiKey, Pin (use user-secrets in dev, env vars in production)
- `Serilog`: Structured logging with file rotation (30-day retention)
- `ConnectionStrings:RedisConnection` or env var `REDIS_CONNECTION`: Redis connection string (default `localhost:6379`)

**Secrets Management**:
- **Development**: Use `dotnet user-secrets` (stored in `~/.microsoft/usersecrets/`)
- **Production**: Use environment variables or secure vaults

## Dependency Injection Container Registrations

Key services to understand when extending:
- `ITheTvDbApiClient` (typed HttpClient, 30s timeout, JSON Accept header)
- `TheTvDbClientState` (**Singleton** — token cache + request throttle)
- `ITheTvDbService` (Scoped, depends on ITheTvDbApiClient + TheTvDbClientState)
- `IDistributedCacheJson` (**Singleton**, backed by Redis)
- `StackExchange.Redis.IConnectionMultiplexer` (**Singleton**, for distributed locking)
- `ICurrentUserService` (Scoped, reads `IHttpContextAccessor`)
- `IAppUserRepository` (Scoped, registered directly in `Program.cs`)
- `ITrackedSeriesRepository` (Scoped, registered via `AddApplicationServices()`)
- `IEpisodeWatchRepository` (Scoped, registered via `AddApplicationServices()`)
- `AppDbContext` (Scoped, PostgreSQL with split queries enabled)
- Razor Pages (Automatically registered for controllers/pages)

## Logging & Diagnostics

**Serilog Configuration** (`appsettings.json`):
- Outputs to **Console** and **File** (`Logs/log-*.txt`, daily rotation)
- Request logging enabled via `UseSerilogRequestLogging()`
- Enrichers: Machine name, Process ID, Thread ID

**Usage in code**:
```csharp
logger.LogWarning(ex, "TheTVDB API error while searching for query '{Query}'.", Query);
```

## Common Tasks

### Add a New Page
1. Create `.cshtml` view in `Pages/`
2. Create `.cshtml.cs` page model inheriting `PageModel`
3. Use constructor injection for dependencies
4. Define handlers (`OnGetAsync`, `OnPostAsync`, etc.)
5. Use `this.SetSuccessToast(...)` / `this.SetErrorToast(...)` for user feedback

### Add a New Service
1. Create interface in `Services/` or `Infrastructure/`
2. Create implementation class
3. Register in `Program.cs` or an extension method
4. Inject into pages or other services

### Add Database Entity
1. Create entity class in `Infrastructure/Persistence/Entities/`
2. Add `DbSet<T>` to `AppDbContext`
3. Create configuration class implementing `IEntityTypeConfiguration<T>` in `Configurations/`
4. Add mapping extension methods in `Recall.Web/Mappings/`
5. Run: `dotnet ef migrations add YourMigration --project Recall.Web`
6. Update database: `dotnet ef database update --project Recall.Web`

### Fix "Permission denied" Docker Error
```bash
# If DataProtection-Keys access denied:
docker exec -u root <container> chown -R 1000:1000 /home/devuser/.aspnet/.
```

## Critical Developer Knowledge

- **Entity Tracking**: EF Core tracks entities by default; be aware of query results being cached during request lifetime
- **Split Query Behavior**: Multiple queries are issued to prevent Cartesian joins (see `UseQuerySplittingBehavior`)
- **Null Reference Handling**: Project uses nullable reference types enabled (`<Nullable>enable</Nullable>`)
- **Async/Await**: All I/O operations accept `CancellationToken` for graceful shutdown
- **Error Boundaries**: Catch typed exceptions (`TheTvDbApiException`) before generic `Exception`
- **TheTvDbClientState is Singleton**: Do not accidentally register it as Scoped/Transient — it holds the cached Bearer token and request throttle that must survive across typed-client instances
- **xmin concurrency token**: `TrackedSeriesEntity.Version` (`uint`) is backed by PostgreSQL's `xmin` system column — do not set it manually; it is managed by Npgsql
- **DevAuthMiddleware in DEBUG only**: Auto-injects a dev user via `#if DEBUG`; no login flow is needed locally

## Project-Specific Conventions NOT to Miss

1. **Always pass `CancellationToken`** to async methods
2. **Use `sealed` modifier** on concrete service classes when appropriate
3. **Validate query strings** with `[StringLength]` attributes on Razor Page properties
4. **Use primary constructor syntax** (C# 12+): `public sealed class MyService(IDependency dep)`
5. **Map external DTOs in service layer**, not in pages
6. **Log with structured parameters**: `LogWarning(ex, "Message with '{Param}'", param)`
7. **Use toast extension methods** (`this.SetSuccessToast`, `this.SetErrorToast`, `this.SetInfoToast`) for user-facing feedback — never write directly to `TempData`
8. **Repositories return domain models** — mapping from entities happens inside the repository; callers never touch EF entities directly
9. **C# 14 `extension` block syntax** is used in `PageModelToastExtensions.cs` — do not convert to the older `this T` style

## External Dependencies & Integration

- **TheTVDB API v4** (https://api4.thetvdb.com/v4/): Requires API key + PIN
- **PostgreSQL 10+**: Connection string via `DefaultConnection` config
- **Redis 7+**: Required at startup; connection via `REDIS_CONNECTION` env var or `ConnectionStrings:RedisConnection`
- **Serilog Sinks**: Console and File sinks configured
- **Npgsql**: PostgreSQL provider with dynamic JSON support enabled
- **Authentication**: passwordless magic-link sign-in (`IPasswordlessAuthService` + `login_token` table); no password hashing

