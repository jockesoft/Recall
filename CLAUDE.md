# CLAUDE.md

Onboarding context for AI sessions working in this repository. Everything below was read from the source on 2026-10-01 (commit `f3e3f57`); claims marked **(inference)** were not seen directly. Where this file and `AGENTS.md` / `README.md` disagree, trust this file and the source.

**Recall.nu** is an ASP.NET Core 10 Razor Pages app for tracking TV series and movies: search TheTVDB, build a library, mark episodes and movies watched, like and rate (1–10), get in-app notifications for newly aired episodes, import an IMDb list export, and see TheTVDB and IMDb (via OMDb) ratings side by side. Sign-in is passwordless (emailed magic link) only.

## 1. Solution layout

`Recall.sln` contains two projects. There is no `global.json`, `Directory.Packages.props`, `Directory.Build.props` or `.editorconfig`.

| Project | Role | References |
|---|---|---|
| `Recall.Web` (`Microsoft.NET.Sdk.Web`) | The whole application: UI, services, persistence, jobs | none |
| `Recall.Tests` (`Microsoft.NET.Sdk`) | NUnit unit and persistence tests | `Recall.Web` |

Layering inside `Recall.Web` is by folder and namespace, not by assembly:

| Folder | Contents |
|---|---|
| `Pages/` | Razor Pages, page models, partials and their small view-model classes (`Pages/Shared/*Model.cs`) |
| `Services/` | Business logic, plus the external HTTP clients under `Services/External/{TheTvDb,Omdb}` |
| `Infrastructure/` | EF Core (`Persistence/`), Quartz jobs (`Timers/`), options classes, Redis JSON cache, auth helpers, CSV parser |
| `Domain/` | Plain models: `TheTvDb/`, `Omdb/`, `Internal/` |
| `Mappings/` | Static extension methods for DTO ↔ domain ↔ entity (no AutoMapper) |
| `Extensions/` | DI registration extension methods, toast helpers |
| `Middleware/` | `DevAuthMiddleware` only |
| `Migrations/` | 22 EF Core migrations plus the model snapshot |

## 2. Stack and versions

- **Framework**: `net10.0` in both projects, nullable and implicit usings enabled. No SDK pin; CI uses `10.0.x`. C# 14 features are in use (`extension` blocks in `Extensions/PageModelToastExtensions.cs`, primary constructors everywhere).
- **Packages that matter** (`Recall.Web/Recall.Web.csproj`): EF Core 10.0.12, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, `Microsoft.Extensions.Caching.StackExchangeRedis` 10.0.12, `Quartz` 4.3.0, `Microsoft.Extensions.Http.Resilience` 10.10.0, `Serilog.AspNetCore` 10.0.0 with Console and File sinks.
- **Referenced but unused in code**: `Swashbuckle.AspNetCore`, `NuGet.Packaging`, `NuGet.Protocol`, `Microsoft.VisualStudio.Web.CodeGeneration.Design`.
- **Tests** (`Recall.Tests/Recall.Tests.csproj`): NUnit 5.0.0, Moq 4.21.0, AwesomeAssertions 9.6.0, `Microsoft.EntityFrameworkCore.Sqlite`, coverlet. `Microsoft.AspNetCore.Mvc.Testing` is referenced but no test uses `WebApplicationFactory`.
- **Front end**: no npm, bundler or Tailwind. Vendored files in `wwwroot/lib`: Bootstrap 5.3.3, jQuery 3.7.1, jquery-validation (+ unobtrusive), Font Awesome Free 6.4.2. Custom CSS in `wwwroot/css/tvdb-theme.css` (about 2,500 lines, imports Google Fonts) and `site.css`. JavaScript is one small file (`wwwroot/js/tvdb-type-filter.js`) plus inline `<script>` blocks in pages; `site.js` is empty.

## 3. Hosting and startup (`Recall.Web/Program.cs`)

**Registration order**

1. Serilog from configuration (`UseSerilog`, console + rolling daily file).
2. `AddRazorPages`, `AddControllers().AddViewLocalization()`, `AddAntiforgery`, `AddHttpContextAccessor`.
3. `AddTrustedForwardedHeaders`: X-Forwarded-For/Proto/Host, applied only when the request comes from a trusted proxy. The `TrustedProxies` section (`Infrastructure/Hosting/TrustedProxyOptions.cs`) lists `Addresses` and `Networks`; with both empty the default is loopback plus the private ranges. An invalid entry fails startup.
4. `AddCookieAuthentication`, `AddAppSession`, `AddAuthorization` (no fallback policy).
5. `AddRedisCache`, `AddPostgres` — both throw at startup if their connection string is missing.
6. TheTVDB (`AddTheTvDb`: `TheTvDbClientState` singleton, typed client with a retry pipeline), `AddOmdb`, `AddApplicationServices`, `AddNotifications`, `AddMail`, `AddWatchlistImport`.
7. Health checks, `AddPasswordlessAuth`, `AddLoginRateLimiting`, `IAppUserRepository`, `AddScheduledJobs` (Quartz).

All of these live in `Extensions/ServiceCollectionExtensions.cs` (application services) and `Extensions/InfrastructureServiceCollectionExtensions.cs` (Redis, Postgres, cookies, session, rate limiting, Quartz). Add new integrations there, not inline in `Program.cs`.

**Lifetimes**

| Lifetime | Services |
|---|---|
| Singleton | `TheTvDbClientState`, `IDistributedCacheJson`, `IConnectionMultiplexer`, `IOmdbRequestBudget`, `ILoginAbuseGuard` |
| Scoped | `AppDbContext`, all repositories, all services, snapshot stores, `ICurrentUserService`, `RecallCookieEvents` |
| Transient (typed `HttpClient`) | `ITheTvDbApiClient`, `IOmdbApiClient`, `ITurnstileVerifier` |
| Factory | `IDbContextFactory<AppDbContext>`, for code that fans out in parallel |

**Options binding**: `Configure<T>(GetSection(T.SectionName))` for `TheTvDbOptions` (`TheTvDb`), `OmdbOptions` (`Omdb`), `MailOptions` (`Mail`), `LoginTokenOptions` (`Login`), `TurnstileOptions` (`Turnstile`). No options validation, except `TrustedProxyOptions` (`TrustedProxies`), which is read and validated eagerly at registration.

**Before serving**: `await app.MigrateDatabaseAsync()` applies pending migrations with 10 retries, 3 s apart. Disable with `Database:MigrateOnStartup=false`.

**Middleware pipeline**: developer exception page (Development) or exception handler that logs and redirects to `/Error` + HSTS → `UseForwardedHeaders` → `UseHttpsRedirection` → `UseStaticFiles` → `UseRouting` → `UseRateLimiter` → `UseSession` → `UseAuthentication` → `DevAuthMiddleware` (Debug builds only) → `UseAuthorization` → endpoints.

**Endpoints**: `/health` (runs `DbHealthCheck`, a `SELECT 1`), `/health/live` (no checks), a default controller route (there are no controllers), `MapStaticAssets`, `MapRazorPages`.

## 4. UI layer

Razor Pages only. No MVC controllers, Blazor or minimal APIs. Two handlers return JSON for inline `fetch` calls: `Series/Details?handler=CheckPriorEpisodes` and `Account/EditProfile?handler=CheckUsername`.

| Page | Access | Purpose |
|---|---|---|
| `/` (`Pages/Index`) | anonymous | Landing page; redirects signed-in users to `/Dashboard` |
| `/Dashboard` | `[Authorize]` | Upcoming episodes (30 days) and the catch-up queue |
| `/Search` | `[Authorize]` | TheTVDB series + movie search (GET form) |
| `/Library` | `[Authorize]` | Watching / Up to date / Watched sections |
| `/Series/Details/{id:int}` | **anonymous read** | Seasons, episodes, progress, like, rating, library toggle |
| `/Episodes/Details/{id:int}` | **anonymous read** | Episode detail, prev/next, watched, like, rating, IMDb score |
| `/Movies/Details/{id:int}` | **anonymous read** | Movie detail, watched, like, rating |
| `/Account/Login`, `/Account/Verify` | anonymous | Request and redeem the magic link |
| `/Account/Logout` | none | Signs out on GET or POST |
| `/Account/Profile`, `EditProfile`, `Favorites`, `Notifications`, `ImportWatchlist` | `[Authorize]` | Account area |
| `/Admin` | `[Authorize(Roles = Roles.Admin)]` | User counts |
| `/sitemap.xml` (`Pages/Sitemap`) | anonymous | Dynamic sitemap from the cache tables |
| `/Privacy`, `/Error` | anonymous | Static |

- The three Details pages have no `[Authorize]`. Their POST handlers check `ICurrentUserService.IsAuthenticated` by hand and redirect with an error toast.
- **Layout** (`Pages/Shared/_Layout.cshtml`): nav, footer attributions, cookie notice, and per-page SEO tags from `ViewData["Title"|"Description"|"Robots"]`. Robots defaults to `noindex, nofollow`; Index, Login, Privacy and the three Details pages opt in. The build number is read from `build.txt` next to the binaries.
- **Partials** (`Pages/Shared/`): `_SeriesCard`, `_CatchUpCard`, `_UpcomingEpisodeCard`, `_FavoriteEpisodeRow`, `_EpisodeWatchedToggle`, `_LikeToggle`, `_RatingWidget`, `_TypeFilterBar`, `_NotificationBell` (injects `INotificationService` and runs an unread `COUNT` on every signed-in page render), `_ToastMessages`. Each takes a small model class from the same folder.
- **Forms**: plain `<form method="post" asp-page-handler="…">`, then redirect (PRG). Antiforgery is the Razor Pages default; inline `fetch` calls copy `__RequestVerificationToken` from the page.
- **Validation**: data annotations on `[BindProperty]` properties (`Login`, `Search`, `EditProfile`) with jQuery unobtrusive validation on the client. Most action handlers take route/form primitives and validate by hand (`if (value is < 1 or > 10)`).
- **Feedback**: `this.SetSuccessToast/SetErrorToast/SetInfoToast(...)` write TempData keys that `_ToastMessages` renders. `SetSuccessToastWithWatchedUndo(message, seriesId, batch)` adds an Undo button to the toast: a POST form to `Series/Details?handler=UndoWatched`, shown when the bulk mark inserted more than one row.

## 5. Domain model

All user data hangs off `AppUserEntity` (Guid PK, equal to the `NameIdentifier` claim), with cascade delete.

| Entity (table) | Meaning | Unique key |
|---|---|---|
| `AppUserEntity` (`app_user`) | Username, email, `Role` (`User`/`Admin`, stored as string) | email; username |
| `TrackedSeriesEntity` (`tracked_series`) | A series in the user's library, with denormalized name/overview/image/first-aired. `Version` is an `xmin` concurrency token; never set it by hand | (user, tvdb id) |
| `EpisodeWatchEntity` (`episode_watch`) | One watched episode, with `WatchedUtc` | (user, episode) |
| `UserMovieWatchEntity` (`user_movie_watch`) | One watched movie | (user, movie) |
| `UserLikeEntity` (`user_like`) | Like on a `Series`, `Episode` or `Movie` (`LikeTargetType`) | (user, type, target) |
| `UserRatingEntity` (`user_rating`) | 1–10 rating, same target shape, DB check constraint | (user, type, target) |
| `NotificationEntity` (`notification`) | In-app notification (only type: `NewEpisode`) | — |
| `NotifiedEpisodeEntity` (`notified_episode`) | Ledger making new-episode notifications idempotent | (user, episode) |
| `LoginTokenEntity` (`login_token`) | SHA-256 hash of a magic-link token, expiry, consumed time | token hash |
| `EmailEntity` (`email`) | Outbound mail queue | — |
| `WatchlistImportJobEntity` / `WatchlistImportItemEntity` | IMDb CSV import job and its rows | — |
| `Cached*Entity` (7 tables) | Postgres tier of the metadata caches; `jsonb` payload. Not user data, safe to rebuild | tvdb id (+ language for aggregates) |

**There are no series, season, episode or movie tables.** Metadata exists only as TheTVDB ids on user rows plus JSON snapshots in the cache tables, deserialized into `Domain/TheTvDb` records (`SeriesAggregate`, `Series`, `Episode`, `MovieAggregate`).

**"Watching" vs "watched" is derived, never stored.** `LibraryModel.ClassifyTrackedSeriesAsync` computes it per request:

- **Watching**: tracked series with at least one aired episode not marked watched.
- **Up to date**: nothing unwatched and TheTVDB status is not "Ended".
- **Watched**: nothing unwatched and status is "Ended"; watched movies are listed here too.

**Progress** is computed by `Services/WatchTracking/WatchProgressCalculator.Build`: order episodes with `OrderBySeasonAndEpisode` (unknown numbers last, tie-break by id), keep those aired on or before today, and the next episode to watch is the first of those without an `EpisodeWatch` row. Episodes flagged `IsMovie` are excluded. The episode list always comes from the series aggregate.

**Watch writes go through `IWatchProgressService`**, never straight to `IEpisodeWatchRepository`: `MarkEpisodeWatchedAsync` / `ToggleEpisodeWatchedAsync` first verify the episode belongs to the submitted series (the aggregate, falling back to the episode's own record) and reject a future air date. `MarkWatchedThroughAsync` marks the target and every earlier episode, skipping any with a future air date.

**Bulk marks and undo.** `MarkSeasonWatchedAsync` / `MarkSeasonUnwatchedAsync` act on one season of the aggregate. Every row a bulk mark inserts (`EpisodeWatchRepository.MarkWatchedRangeAsync`) shares one `WatchedUtc`, truncated to the millisecond and returned as a `WatchedBatch`; `UndoWatchedBatchAsync` deletes exactly the user's rows in that series with that timestamp. Keep the truncation: Postgres stores microseconds, so an untruncated .NET timestamp would not compare equal after a round trip (SQLite tests cannot show this; it was verified by hand against Postgres).

Movies have no library row. A movie is watched (`UserMovieWatch`), liked or rated, and nothing else.

## 6. Data access

- **ORM**: EF Core 10 on PostgreSQL via Npgsql. One context, `Infrastructure/Persistence/AppDbContext.cs`. No Dapper or raw SQL.
- **Configuration**: one `IEntityTypeConfiguration<T>` per entity in `Persistence/Configurations/`, applied with `ApplyConfigurationsFromAssembly`. Tables are singular snake_case, columns snake_case, enums stored as strings, timestamps `timestamp with time zone`.
- **Context options** (`AddPostgres`): shared `NpgsqlDataSource` with `EnableDynamicJson()`, `SplitQuery` by default, and `MultipleCollectionIncludeWarning` raised to an exception.
- **Audit timestamps**: `SaveChanges`/`SaveChangesAsync` set `CreatedUtc`/`UpdatedUtc` for nine entity types. `ExecuteUpdateAsync` bypasses this, so those calls set `UpdatedUtc` themselves.
- **Two ways to get a context**:
  - Repositories and `EpisodeOmdbSnapshotStore` inject the scoped `AppDbContext`. Calls on it must stay sequential.
  - `TvdbSnapshotStore`, `OmdbSnapshotStore`, `MovieOmdbSnapshotStore` and `SitemapService` use `IDbContextFactory` and open a context per call, so callers may run them under `Task.WhenAll`.
- **Repositories** (`Persistence/Repositories/`): interface + sealed implementation, return domain models or small records, reads use `AsNoTracking`. Exception: `IAppUserRepository` returns `AppUserEntity`. `Pages/Admin/Index` injects `AppDbContext` directly.
- **Concurrency idiom**: check, insert, then catch `DbUpdateException` whose inner `PostgresException` is `UniqueViolation` and treat it as success. Atomic state changes use `ExecuteUpdateAsync` (`LoginTokenRepository.MarkConsumedAsync`).
- **Migrations**: generate with the CLI; never hand-write (the `Designer.cs` and snapshot must match). They run automatically at startup. There is no design-time factory (`AppDbContextFactory` is commented out), so `dotnet ef` builds the host through `Program.cs`.
- **Seeding**: none. In local dev the hardcoded dev user row must be inserted by hand (see section 12).

**Query patterns worth knowing before optimizing**

- No classic N+1 over navigation properties; no `Include` calls anywhere.
- `Dashboard`, `Library`, `Favorites` and `Profile` (via `WatchTimeService`) load one full `SeriesAggregate` JSON per tracked/liked/watched series with `Task.WhenAll` on every request. Each is a Redis GET plus deserialization of a payload holding every episode and character.
- `NewEpisodeNotificationTimer` loops series, then users, with one watched-ids query and one or two notification queries per pair.
- `SitemapService.GetCachedEpisodesAsync` returns every row of `cached_episode_extended` with no limit.
- `RatingRepository.GetSummaryAsync` runs `COUNT` then `AVG` as two queries.
- `WatchlistImportRepository.RecalculateJobProgressAsync` reloads every item status for the job after each batch.

## 7. External integrations

### TheTVDB (v4 API)

- `Services/External/TheTvDb/TheTvDbApiClient.cs` is pure transport: typed `HttpClient`, 30 s timeout, bearer token attached per request.
- `TheTvDbClientState` **must stay a singleton**. It holds the cached token and a `SemaphoreSlim(5)` throttle shared by all client instances. On a 401 the client passes the specific stale token back, so only one of several racing requests re-authenticates; the request is retried once.
- Non-success responses throw `TheTvDbApiException`.
- **Resilience** (`Infrastructure/External/ExternalHttpResilience.cs`, `Microsoft.Extensions.Http.Resilience`): TheTVDB GETs are retried up to 3 times on network errors, timeouts, 408, 5xx and on a 429 whose `Retry-After` is at most 5 s; each attempt is capped at 10 s and `HttpClient.Timeout` (30 s) bounds the whole call. The login POST is not retried. OMDb gets one retry and never on 429, because a retry does not take a permit from `IOmdbRequestBudget`. A new external client should add its own pipeline there.
- `Services/TheTvDbService.cs` owns the read path through `GetLayeredAsync<T>`: **Redis → Postgres snapshot → API**. A database or API hit is written back up. Nulls are not cached.

| Resource | Redis key (instance prefix `tvdb:`) | Redis TTL (±10% jitter) | Postgres table |
|---|---|---|---|
| Series aggregate | `series:aggregate:v1:{id}:{lang}` | 12 h; 7 d if ended and not keep-updated | `cached_series_aggregate` |
| Movie aggregate | `movie:aggregate:v1:{id}:{lang}` | 12 h; 7 d if released | `cached_movie_aggregate` |
| Series extended | `series:extended:v2:{id}` | 12 h | `cached_series_extended` |
| Episode extended | `episode:extended:v2:{id}:{lang}` | 12 h | `cached_episode_extended` |

- **The Postgres tier has no staleness check on read.** A row is served until something explicitly refreshes it: `Refresh*ByIdAsync` or the hourly jobs, which revisit keep-updated aggregates after 12 h and all others after 30 d. The read path's `Save*` methods are insert-only; only the refresh path upserts. A mapping change therefore does not reach already-cached rows until they are refreshed, which can take up to 30 days plus queue time.
- Language is hardcoded to `eng`. Episode names are translated per episode, reusing `cached_episode_extended` before calling the API.
- **Image URLs**: TheTVDB returns absolute and relative paths inconsistently. Every image field goes through `ArtworkUrl.Normalize` at DTO → domain mapping time, and again on every service read via `DomainImageNormalization.WithNormalizedImages()` so old cached rows heal. A new image-bearing field needs both.
- `SearchAsync` and `ResolveByRemoteIdAsync` are not cached.

### OMDb

- `Services/External/Omdb/OmdbApiClient.cs`: typed client, 20 s overall timeout (8 s per attempt, one retry), API key in the query string, returns `null` for "not found". It has no cache of its own.
- Snapshots live in `cached_series_omdb`, `cached_movie_omdb` and `cached_episode_omdb`, refreshed at most every 30 days. A row with a null payload records "checked, nothing found".
- Series and movies are enriched proactively by hourly jobs. **Episodes are enriched lazily on the request path**, the first time `Episodes/Details` is opened (`DetailsModel.LoadOmdbAsync`).
- Every OMDb call site must first take a permit from the singleton `IOmdbRequestBudget` (`FixedWindowRateLimiter`, default 900/day via `Omdb:MaxRequestsPerDay`). A new call site must do the same.

### Cloudflare Turnstile

`Services/Authentication/TurnstileVerifier.cs` posts to `siteverify` from the login page, with a 10 s timeout and no retry. It is disabled when either key is blank and fails closed on network errors.

### SMTP

`Services/MailService.cs` uses `System.Net.Mail.SmtpClient`. In Development it writes `.eml` files to `Recall.Web/mail-pickup/` instead of sending.

### Keys

User secrets in development (`UserSecretsId` in the csproj); environment variables from `.env.prod` in production (`TheTvDb__ApiKey`, `TheTvDb__Pin`, `Omdb__ApiKey`, `Mail__*`, `Turnstile__*`).

## 8. Authentication and authorization

- No ASP.NET Core Identity, external providers or JWT. Cookie authentication only (`AddCookieAuthentication`): cookie `Recall.Auth`, 30-day sliding expiry, HttpOnly, SameSite=Lax, Secure in Release builds. Login and access-denied paths are both `/Account/Login`.
- **Cookie revalidation**: `Infrastructure/Authentication/RecallCookieEvents.cs` (`options.EventsType`) re-reads the user row at most every 5 minutes per session. A missing user is signed out; a changed username, email or role is reissued into the cookie. The last-check time is stored in the cookie's own properties. A database error keeps the session and retries on the next request. `RecallPrincipal.Create` is the single definition of the claim set; use it when issuing a cookie.
- **Request a link** (`PasswordlessAuthService.RequestLoginAsync`): normalize email → optional allowlist (`Login:AllowedEmails`; empty means open registration) → `ILoginAbuseGuard` per-address daily cap and site-wide hourly cap → get or create the user → per-user resend cooldown → invalidate earlier tokens → store the SHA-256 hash of a 32-byte random token → queue the email. The page shows the same "link sent" result in every case.
- **Redeem** (`RedeemAsync`, `Pages/Account/Verify`): look up an unconsumed, unexpired hash, then `MarkConsumedAsync` (atomic `UPDATE … WHERE ConsumedUtc IS NULL`). The result of that update is checked so two simultaneous redemptions cannot both succeed. Then `SignInAsync` with `NameIdentifier`, `Name`, `Email` and `Role` claims.
- **Bot defenses on the login form**: honeypot field, minimum 2 s render-to-submit time, Turnstile, per-IP rate limit (`login-email` policy, 8 per 5 minutes) and a global limiter on POSTs to `/Account/Login` (300 per minute).
- **Roles**: `User` and `Admin` (`UserRole` enum; `Roles` constants for attributes). Promotion to Admin is a manual database edit.
- **Per-user scoping**: `ICurrentUserService.UserId` (parsed from the claim) is passed into every repository call, and every user-data query filters on `UserId`. There are no global query filters.
- **`DevAuthMiddleware`**: in Debug builds every non-file request runs as a fixed admin (`11111111-1111-1111-1111-111111111111`, `dev-user`, `dev@example.com`). It skips itself when `ASPNETCORE_ENVIRONMENT=Test` and is compiled out of Release. It does not create the user row.

## 9. Background work

Quartz.NET with the default in-memory store, registered in `AddScheduledJobs()`. Implementations are in `Infrastructure/Timers/`, all `[DisallowConcurrentExecution]`. The hosted service waits for running jobs on shutdown.

| Job | Interval | Work per run |
|---|---|---|
| `UpdateTvDbInfoTimer` | 60 min | Up to 10 series aggregates: `KeepUpdated = true` and older than 12 h, or any other row older than 30 d; tracked series first, then oldest. Then up to 25 episodes older than 30 d, or titled "TBA" and older than 12 h, or aired without an image (max 5 attempts) |
| `UpdateMovieInfoTimer` | 60 min | Up to 10 movie aggregates, same two tiers; movies someone has watched or liked first |
| `UpdateOmdbInfoTimer` | 60 min | Up to 30 series whose OMDb snapshot is missing or older than 30 d |
| `UpdateMovieOmdbInfoTimer` | 60 min | Same, for movies |
| `MailTimer` | 1 min | Sends up to `Mail:BatchSize` (20) queued emails; gives up after `MaxSendAttempts` (5) |
| `NewEpisodeNotificationTimer` | 6 h | For up to 500 tracked series, notifies each tracking user about episodes aired in the last 3 days that they have not watched; one notification per series per user |
| `WatchlistImportTimer` | 1 min | Resolves up to 15 pending import rows through TheTVDB's remote-id search |

There are no other hosted services, queues or message brokers. The email and import "queues" are database tables.

## 10. Configuration and secrets

- `appsettings.json`: defaults, including the local dev connection strings (`Host=localhost…Password=devpassword`, `localhost:6379`) and empty API keys.
- `appsettings.Development.json`: log levels only. `appsettings.Production.json`: Redis at `redis:6379`, empty `DefaultConnection`, log file under `/var/log/recallapp`.
- Redis connection: `REDIS_CONNECTION` environment variable, falling back to `ConnectionStrings:RedisConnection`.
- Trusted reverse proxies: `TrustedProxies__Networks__0`, `TrustedProxies__Addresses__0`, … Setting either list replaces the built-in default (loopback + private ranges) entirely. `RemoteIpAddress`, and so every per-IP rate limiter, is only as trustworthy as this list.
- Production secrets come from `.env.prod`, loaded by `compose.prod.yml`. `.gitignore` excludes `.env*`; `.env` and `.env.prod` exist in the working directory but are not tracked.
- **No real secret is committed.** The only credential in the repository is the local-dev Postgres password `devpassword` (also in `README.md`).

## 11. Testing

- 277 tests in `Recall.Tests`, all passing as of this writing. NUnit + Moq + AwesomeAssertions. Folders mirror `Recall.Web`.
- **Persistence tests** use a real `AppDbContext` on in-memory SQLite (`SqliteConnection("DataSource=:memory:")` + `EnsureCreatedAsync`), not mocks. See `LoginTokenRepositoryTests.cs` for the pattern.
- **Covered**: `PasswordlessAuthService`, `MailService`, `TheTvDbService`, `TheTvDbApiClient`, `TheTvDbClientState`, snapshot stores, watch progress and watch time, notifications, favorites, sitemap, watchlist import and CSV parser, mappings, health check, OMDb budget, trusted forwarded headers (run through the real `ForwardedHeadersMiddleware`).
- **Not covered**: every page model, all Quartz jobs, `LoginAbuseGuard`, `TurnstileVerifier`, `OmdbApiClient`, `DevAuthMiddleware`, the `Like`/`Rating`/`Notification`/`TrackedSeries`/`AppUser`/`Email` repositories, and anything through the HTTP pipeline. Postgres-only behavior (`jsonb`, `xmin`, the `UniqueViolation` catch blocks) is not exercised by SQLite. `TrackedSeriesEntity` cannot be inserted through EF on SQLite at all (`xmin` becomes an ordinary NOT NULL column); tests that need a tracked series seed it with raw SQL, see `TvdbSnapshotStoreTests`.

```bash
dotnet test Recall.sln                                              # everything
dotnet test Recall.Tests --filter "FullyQualifiedName~ClassName"    # one fixture
dotnet test Recall.Tests --filter "Name=MethodName"                 # one test
cd Recall.Tests && ./run-tests-with-coverage.sh                     # HTML report (needs the reportgenerator tool)
```

## 12. Build, run and deploy

**One-time local setup**

```bash
dotnet user-secrets set "TheTvDb:ApiKey" "..." --project ./Recall.Web
dotnet user-secrets set "TheTvDb:Pin" "..." --project ./Recall.Web
dotnet user-secrets set "Omdb:ApiKey" "..." --project ./Recall.Web
dotnet user-secrets set "Login:AllowedEmails:0" "dev@email.com" --project ./Recall.Web

docker run --name my-redis -p 6379:6379 -d redis:7
docker run --name local_postgres -p 5432:5432 \
  -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=devpassword -e POSTGRES_DB=recall_db \
  -v pgdata:/var/lib/postgresql -d postgres:18.1
```

After the first start has created the schema, insert the dev user row into `app_user` (`11111111-1111-1111-1111-111111111111`, `dev-user`, `dev@example.com`). Without it, anything that writes user data fails on the foreign key.

**Run, build, migrate**

```bash
dotnet watch run --project Recall.Web --launch-profile Recall.Web   # https://localhost:7123
dotnet build Recall.sln --configuration Release
dotnet ef migrations add <Name> --project Recall.Web --startup-project Recall.Web
dotnet ef database update --project Recall.Web --startup-project Recall.Web
```

**CI/CD** (`.github/workflows/dotnet.yml`): on push and pull request to `main`, restore, Release build, test with coverage (summary only; coverage does not gate). On push, build `Dockerfile.prod`, push `ghcr.io/<user>/recall:latest` and `:<run number>`, then SSH to the server, run `dump_db.sh` and `docker compose -f compose.prod.yml up -d --pull always`. There is no lint or format step. `nightly-build.yml` builds and tests daily at 05:00 UTC.

**Hosting**: a single Docker host. `compose.prod.yml` runs the app (port 8701, logs and Data Protection keys on bind mounts), `postgres:18.1` (host port 5433) and `redis:7`. Both published ports are bound to `127.0.0.1`, so the reverse proxy on the host is the only way in. CI does not copy `compose.prod.yml` to the server; the copy in `recall-deploy/` there is maintained by hand. `Dockerfile.prod` expects `build.txt` in the build context, which only CI writes.

`Recall.Web/Dockerfile` and `compose.yaml` are IDE-generated and not used by CI.

## 13. Conventions

- **Layering**: `Pages` → `Services` → `Infrastructure` → `Domain`/`Mappings`. In practice page models inject repositories directly for simple reads and writes; services exist where there is real logic.
- **Class style**: `sealed` classes with primary constructors; interface and implementation in sibling files; records for value shapes; `CancellationToken cancellationToken = default` as the last parameter on every async method; `Async` suffix throughout.
- **Nullable reference types** are on. `null` means "not found"; exceptions mean failure.
- **DI**: register through an `AddXxx` extension method. Options classes expose `const string SectionName`.
- **Mapping**: static `ToDomain()` / `ToEntity()` / `ToAggregate()` extension methods in `Mappings/`.
- **JSON**: always `RecallJsonOptions.Web`, so Redis and Postgres payloads stay compatible.
- **User feedback**: `this.SetSuccessToast/SetErrorToast/SetInfoToast(...)`; never write to `TempData` directly.
- **Error handling in page handlers**: wrap in `try`, log, set an error toast, redirect or re-render. Jobs and fan-outs use `catch (Exception ex) when (ex is not OperationCanceledException)` so one bad item never aborts a batch.
- **Best-effort external calls**: reuse `ITheTvDbService.TryGetSeriesAggregateAsync` / `TryGetMovieAggregateAsync` (swallow and log) and `Task<T?>.AsOptionalAsync(...)` (swallows `TheTvDbApiException` only). A primary fetch failure should still propagate.
- **Episode ordering**: `EpisodeOrderingExtensions.OrderBySeasonAndEpisode` is the single implementation; reuse it.
- **Recording a watch**: call `IWatchProgressService`; it validates the series/episode pair and the air date. Page handlers map the returned `EpisodeWatchOutcome` to a toast.
- **Parallelism**: only fan out over code that uses `IDbContextFactory`. Never run two operations on the scoped `AppDbContext` at once.
- **Logging**: structured message templates with `ILogger<T>`; no string interpolation in log calls.
- **Time**: persistence and jobs use `DateTime.UtcNow`. Air-date comparisons use `AirDate.Today` / `AirDate.IsInFuture` (`Services/WatchTracking/AirDate.cs`), the UTC date; do not use `DateTime.Today` for them. An unknown air date is not treated as unaired.
- **Comments**: the codebase explains *why* in comments and XML docs on non-obvious code; keep that density.
- **SEO**: pages are `noindex` unless they set `ViewData["Robots"]`.

## Observations

Described only, ranked by impact. Nothing here has been changed.

1. **Anonymous pages can spend upstream quota.** `Series/Details`, `Episodes/Details` and `Movies/Details` are public, indexable and have no rate limit. A request for an uncached id calls TheTVDB and writes Redis and Postgres rows; an episode page can also make a live OMDb call. `/sitemap.xml` lists every cached episode, so crawlers are invited to each one, and the sitemap has no size cap (the protocol limit is 50,000 URLs). The shared OMDb budget caps the damage at 900 calls a day but lets anonymous traffic starve the hourly enrichment jobs.
2. **Forwarded headers: fixed, with a residual default.** Headers are now applied only from trusted proxies and the compose ports are loopback-only. What remains: the default trusts all private ranges, so any other machine or container on a private network that can reach the app could still forge `X-Forwarded-For`; set `TrustedProxies__Networks__0` to the proxy's exact network to close that. The loopback binding only takes effect once the server's own copy of `compose.prod.yml` is updated.
3. **Sessions that could not be revoked: fixed, within 5 minutes.** Deleting or demoting a user takes effect at the next revalidation. There is still no "sign out everywhere" for a user who keeps their account, and no admin UI to do either; both remain manual database edits.
4. **No HTTP resilience: fixed.** Both metadata clients retry transient failures; see section 7. Not added: a circuit breaker, so a long TheTVDB outage still costs every request its retries.
5. **Cached aggregates never refreshing: fixed.** Rows without `KeepUpdated = true` are now refreshed every 30 days. Remaining: `cached_series_extended` still has no refresh path, but nothing reads it any more (`ITheTvDbService.GetSeriesByIdExtendedAsync` has no caller outside tests), so the table and method are candidates for removal. The refresh cap (10 series and 10 movies an hour) bounds how fast a large, crawler-filled cache cycles.
6. **Two sources for a series' episode list: fixed.** Display, progress, "mark watched through" and the prior-unwatched prompt all read the aggregate.
7. **Write handlers trusting client-supplied id pairs: fixed for watches, likes and ratings on episodes.** They go through `IWatchProgressService` or refuse when the parent series can't be resolved. Still unvalidated: series and movie like/rating handlers accept any positive id without checking it exists on TheTVDB.
8. **Single-instance assumptions.** Login abuse limits, the OMDb daily budget, the TheTVDB token and the Quartz schedule are all in process memory, and migrations run at startup. Every deploy resets the OMDb budget and login counters. A second instance would double every job.
9. **Test gaps.** No page-model or pipeline tests although `Mvc.Testing` is referenced; no job tests; SQLite cannot exercise the Postgres-specific branches; CI coverage is informational only.
10. **Dead or unused code and dependencies.** Four unused packages (section 2); `AddControllers` and `MapControllerRoute` with no controllers; `AddSession`/`UseSession` with no session use; `IConnectionMultiplexer` registered "for locking" but never injected; `AppDbContextFactory` fully commented out; `UserItem` and `UserMappings` unreferenced; empty `site.js` and an empty `<script type="importmap">`; stale csproj items (`_LoginPartial.cshtml`, `Services\Models\`).
11. **Convention drift.** `IAppUserRepository` returns entities; `Admin/Index` queries `AppDbContext` directly; control flow by exception message (`ex.Message.Contains("already in your library")`); `OmdbSeries` is also the type for movies and episodes; `EpisodeOmdbSnapshotStore` uses the scoped context while its two siblings use the factory.
12. **State-changing GETs.** `/Account/Logout` signs out on GET; `Notifications?handler=Open` marks a notification read on GET.
13. **Unbounded tables.** `login_token`, `email` (including sent magic-link bodies), `notified_episode`, `notification` and import items are never pruned.
14. **Duplication.** `ApplyAuditTimestamps` repeats the same block nine times; the three OMDb snapshot stores and two OMDb jobs are near copies; the "is authenticated" guard is repeated in every Details POST handler.
15. **Local time for air dates: fixed.** All air-date checks use the UTC date via `AirDate`. There is still no notion of the user's time zone, and the Dashboard header prints the server-local date.
16. **Deployment details.** The image sets a Swedish locale (`sv_SE.UTF-8`), so culture-sensitive date formatting such as the Dashboard's `ToString("dddd, MMMM d")` renders in Swedish on an English site **(inference)**; the log directory is `chmod 777`; the Copilot setup workflow uses `postgres:16-alpine` against 18.1 elsewhere.
17. **Stale documentation and comments.** `AGENTS.md` predates movies, likes, ratings and import. `README.md` backup commands point at a Receptus path. Several comments say jobs are "scheduled in `Program.cs`"; `AddOmdb` says nothing calls OMDb on a request path; `_Layout` says almost every page requires sign-in; `LogoutModel` says the nav links with GET (it posts).
18. **Debug builds log the raw login token** (`PasswordlessAuthService`, inside `#if DEBUG`). Deliberate for local sign-in, but a Debug build must never be deployed.

## Open questions

1. What sits in front of the app in production (reverse proxy, TLS termination), and is port 8701 reachable from outside it?
2. Is registration open in production, or is `Login:AllowedEmails` still populated?
3. Which TheTVDB and OMDb plans are in use, and what are their real rate limits?
4. Are the public Details pages and the full-episode sitemap intended to drive search traffic, and is the resulting upstream API load acceptable?
5. Are movies meant to become first-class library items (a "want to watch" state), or stay as watched/liked/rated only?
6. Should "aired" be judged in UTC, server time or the user's time zone?
7. Is a single app instance a permanent assumption?
8. Is there a data-retention or account-deletion requirement? The code has no account deletion and prunes nothing, and the Privacy page does not mention either.
9. `dump_db.sh` and the server-side `.env.prod` are not in the repository. Where are backups stored and tested?
10. Are the unused packages, the controller route and session registration reserved for planned work (an API, Swagger), or leftovers?
11. Should `AGENTS.md`, `Recall.Web/Dockerfile` and `compose.yaml` be kept?
12. The project memory notes that Recall mirrors account features from the sibling Receptus repository. Which Receptus features are still to be ported?

## Quick facts

- **Stack**: ASP.NET Core 10 Razor Pages, C# 14, one web project plus one test project.
- **Data**: PostgreSQL 18 through EF Core 10 (Npgsql); Redis 7 as a read-through cache; metadata stored as JSON snapshots, not relational tables.
- **External APIs**: TheTVDB v4 (primary metadata), OMDb (IMDb ratings), Cloudflare Turnstile, SMTP.
- **Auth**: passwordless magic link → 30-day cookie; roles `User`/`Admin`; Debug builds auto-sign-in as a fixed admin.
- **Jobs**: seven Quartz.NET jobs, in-memory schedule, single instance.
- **Front end**: Bootstrap 5.3.3 + jQuery, vendored; no build step.
- **Run**: `dotnet watch run --project Recall.Web --launch-profile Recall.Web` (needs local Redis and Postgres) → https://localhost:7123
- **Build**: `dotnet build Recall.sln --configuration Release`
- **Test**: `dotnet test Recall.sln` (277 tests, NUnit, SQLite in-memory for persistence)
- **Deploy**: push to `main` → GitHub Actions → GHCR image → `docker compose -f compose.prod.yml up -d` over SSH.
