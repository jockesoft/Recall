# Agents Guide for Recall Codebase

## Architecture Overview

**Recall** is an ASP.NET Core 10 web application that integrates with TheTVDB API to search and track TV series. It uses:
- **Razor Pages** for the UI layer
- **Entity Framework Core** with PostgreSQL for data persistence
- **Dependency Injection** for service management
- **Serilog** for structured logging
- **NUnit** with Moq for testing

The application follows a **layered architecture**:
- **Pages** (Razor Pages): Handle HTTP requests/responses
- **Services**: Business logic and orchestration
- **Infrastructure**: Data access (EF Core), external API clients
- **Domain**: Models and DTOs

## Key Architectural Patterns

### 1. Service Registration via Extension Methods
Services are registered in `Recall.Web/Extensions/ServiceCollectionExtensions.cs` using extension methods (e.g., `AddTheTvDb()`). This pattern is called in `Program.cs` and centralizes configuration for feature areas.

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
Located in `Recall.Web/Infrastructure/External/TheTvDb/`:
- **ITheTvDbApiClient**: Low-level HTTP client (uses named HttpClient)
- **ITheTvDbService**: High-level service with business logic
- **DTOs**: Separate request/response models for API contracts

The API client uses HttpClientFactory for efficient resource management. The service layer handles filtering (e.g., excludes non-series types) and maps DTOs to domain models.

### 3. Database Layer Pattern
**AppDbContext** (`Recall.Web/Infrastructure/Persistence/AppDbContext.cs`):
- Uses EF Core 10 with PostgreSQL (Npgsql provider)
- **Split Query behavior** enabled to prevent Cartesian explosion
- **Automatic audit timestamps**: `CreatedUtc` and `UpdatedUtc` are set automatically via `ApplyAuditTimestamps()` override
- Entity configurations use `IEntityTypeConfiguration<T>` pattern via `ApplyConfigurationsFromAssembly()`

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
- `CancellationToken` is passed throughout async chains
- Error handling with typed exceptions (e.g., `TheTvDbApiException`)

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

**Secrets Management**:
- **Development**: Use `dotnet user-secrets` (stored in `~/.microsoft/usersecrets/`)
- **Production**: Use environment variables or secure vaults

## Dependency Injection Container Registrations

Key services to understand when extending:
- `ITheTvDbApiClient` (HttpClient-based, 30s timeout, JSON Accept header)
- `ITheTvDbService` (Scoped, depends on ITheTvDbApiClient)
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

### Add a New Service
1. Create interface in `Services/` or `Infrastructure/`
2. Create implementation class
3. Register in `Program.cs` or an extension method
4. Inject into pages or other services

### Add Database Entity
1. Create entity class in `Infrastructure/Persistence/Entities/`
2. Add `DbSet<T>` to `AppDbContext`
3. Create configuration class implementing `IEntityTypeConfiguration<T>` in `Configurations/`
4. Run: `dotnet ef migrations add YourMigration --project Recall.Web`
5. Update database: `dotnet ef database update --project Recall.Web`

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

## Project-Specific Conventions NOT to Miss

1. **Always pass `CancellationToken`** to async methods
2. **Use `sealed` modifier** on concrete service classes when appropriate
3. **Validate query strings** with `[StringLength]` attributes on Razor Page properties
4. **Use primary constructor syntax** (C# 12+): `public sealed class MyService(IDependency dep)`
5. **Map external DTOs in service layer**, not in pages
6. **Log with structured parameters**: `LogWarning(ex, "Message with '{Param}'", param)`

## External Dependencies & Integration

- **TheTVDB API v4** (https://api4.thetvdb.com/v4/): Requires API key + PIN
- **PostgreSQL 10+**: Connection string via `DefaultConnection` config
- **Serilog Sinks**: Console and File sinks configured
- **Npgsql**: PostgreSQL provider with dynamic JSON support enabled

