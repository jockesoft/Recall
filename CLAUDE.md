# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Recall.nu — an ASP.NET Core 10 Razor Pages app for tracking TV shows: search TheTVDB, build a personal library, mark episodes watched, get notified when new episodes air, rate/like series and episodes, and see IMDb (via OMDb) and TheTVDB ratings side by side. Passwordless (magic-link) sign-in only — no passwords anywhere.

There is also an `AGENTS.md` at the repo root with earlier architectural notes; some of it (e.g. page names, the entity list) has drifted from the current code, so prefer this file and the source itself where they disagree.

## Commands

### Setup (one-time, local dev)
```bash
dotnet user-secrets init --project ./Recall.Web
dotnet user-secrets set "TheTvDb:ApiKey" "..." --project ./Recall.Web
dotnet user-secrets set "TheTvDb:Pin" "..." --project ./Recall.Web
dotnet user-secrets set "Omdb:ApiKey" "..." --project ./Recall.Web
dotnet user-secrets set "Login:AllowedEmails:0" "dev@email.com" --project ./Recall.Web

docker run --name my-redis -p 6379:6379 -d redis:7
docker run --name local_postgres -p 5432:5432 \
  -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=devpassword -e POSTGRES_DB=recall_db \
  -v pgdata:/var/lib/postgresql -d postgres:18.1
```
The app fails to start without both Redis and Postgres reachable — `Program.cs` throws a clear config error at startup rather than a confusing runtime error if either connection string is missing (see `AddRedisCache`/`AddPostgres` in `Extensions/InfrastructureServiceCollectionExtensions.cs`).

In `Debug` builds, `DevAuthMiddleware` auto-signs in every request as a hardcoded admin user (`11111111-1111-1111-1111-111111111111` / `dev-user` / `dev@example.com`, role `Admin`) — no login flow needed locally. It does **not** create that user row for you; if the dev DB is empty, insert it manually first (see README) or you'll get FK errors on anything that touches `AppUsers`. This middleware skips itself when `ASPNETCORE_ENVIRONMENT=Test` (see Testing below), and does nothing at all in Release builds (compiled out via `#if DEBUG`).

### Run
```bash
dotnet watch run --project Recall.Web --launch-profile Recall.Web   # https://localhost:7123
```

### Build
```bash
dotnet build Recall.sln --configuration Release
```

### Test
```bash
dotnet test Recall.sln                                              # everything
dotnet test Recall.Tests --filter "FullyQualifiedName~ClassName"    # one fixture
dotnet test Recall.Tests --filter "Name=MethodName"                 # one test
cd Recall.Tests && ./run-tests-with-coverage.sh                     # HTML coverage report -> coverage-report/index.html
```
Tests are NUnit + Moq + AwesomeAssertions. Repository tests spin up a real `AppDbContext` against an in-memory SQLite connection rather than mocking EF Core — see `LoginTokenRepositoryTests.cs` for the pattern (`SqliteConnection("DataSource=:memory:")` + `EnsureCreatedAsync`). Integration-style tests that exercise the full pipeline run with `ASPNETCORE_ENVIRONMENT=Test`, which `DevAuthMiddleware` specifically checks for and disables itself under — without that, every request through `WebApplicationFactory<Program>` would come in pre-authenticated as admin, making auth/authz impossible to test.

### Database migrations
```bash
dotnet ef migrations add <Name> --project Recall.Web --startup-project Recall.Web
dotnet ef database update --project Recall.Web --startup-project Recall.Web
```
Always generate migrations with the CLI rather than hand-writing them — the `Designer.cs` and snapshot files have to match exactly. `MigrateDatabaseAsync()` also runs automatically on app startup (`Program.cs`), so `dotnet ef database update` is mainly for inspecting a migration before it ships.

### Docker / deploy
CI (`.github/workflows/dotnet.yml`) builds `Recall.sln` Release, runs the full test suite with coverage on every push/PR to `main`, then on push builds `Dockerfile.prod` and deploys over SSH via `compose.prod.yml`. There's no separate lint step and no `.editorconfig` — the only gate is build + test.

## Architecture

### Layering
`Pages` (Razor Pages, HTTP in/out) → `Services` (business logic) → `Infrastructure` (EF Core, external HTTP clients, caching, Quartz jobs) → `Domain` (plain models) / `Mappings` (static extension methods converting DTO ↔ domain ↔ entity, no AutoMapper). Repositories (`Infrastructure/Persistence/Repositories/`) always accept and return **domain models, never EF entities** — mapping happens inside the repository implementation.

Services are registered via extension methods on `IServiceCollection` (`Extensions/ServiceCollectionExtensions.cs` for domain/app services, `Extensions/InfrastructureServiceCollectionExtensions.cs` for cross-cutting infra — Redis, Postgres, cookie auth, session, rate limiting, the Quartz job schedule), called from `Program.cs`. When adding a new integration, follow that pattern rather than inlining registration in `Program.cs`.

### TheTVDB integration — three-tier cache, read carefully before touching
`TheTvDbApiClient` (`Services/External/TheTvDb/`) is pure HTTP transport. `TheTvDbClientState` is a **required singleton** holding the cached bearer token and a `SemaphoreSlim(5)` request throttle — it must survive across the typed client's transient instances, or every call re-authenticates. Token refresh after a 401 passes the *specific stale token* being replaced (not a bare `forceRefresh: bool`), so concurrent requests racing to refresh don't all re-login — only the one still holding the token that's known-bad does.

`TheTvDbService` (`Services/TheTvDbService.cs`) owns the actual read path: **Redis → Postgres snapshot (`cached_series_aggregate`/`cached_series_extended`/`cached_episode_extended`) → TheTVDB API**, via the private `GetLayeredAsync<T>` helper. A hit at any tier short-circuits the rest; a DB or API hit gets written back up into Redis. Critically, **the Postgres tier has no staleness check** — a row is treated as good forever until something explicitly re-fetches and upserts it (the `UpdateTvDbInfoTimer` background job, or an explicit `RefreshSeriesAggregateByIdAsync`/`RefreshEpisodeDetailsByIdAsync` call). Keep that in mind before assuming a mapping change takes effect everywhere immediately — old cached rows keep serving whatever they had until refreshed.

Image URLs from TheTVDB are inconsistently absolute-vs-relative depending on field/endpoint. Every raw path must go through `ArtworkUrl.Normalize` (`Services/External/TheTvDb/ArtworkUrl.cs`) — this already happens once at DTO→domain mapping time (`Mappings/SeriesDataDtoMappings.cs`, `Mappings/EpisodeMappings.cs`, `Mappings/SeriesMapping.cs`) and *again* defensively on every `TheTvDbService` read via `DomainImageNormalization.WithNormalizedImages()`, specifically so a row cached before some future mapping fix still gets healed on read instead of serving broken images indefinitely. If you add a new image-bearing field anywhere in this chain, normalize it in both places or it will eventually resurface as a "broken image" bug reported against production.

### OMDb integration — same cache philosophy, deliberately lazier
`OmdbApiClient` is thin HTTP transport with no service-layer cache of its own (unlike TheTVDB). Series enrichment is proactive: `UpdateOmdbInfoTimer` runs hourly, refreshing up to 30 series/run whose `cached_series_omdb` snapshot is missing or >30 days old. Episode enrichment is the opposite — **lazy, on-demand**, fetched the first time someone opens `Episodes/Details` (there are far more episodes than series; eagerly enriching all of them isn't worth the quota) — see `EpisodeOmdbSnapshotStore` and `DetailsModel.LoadOmdbAsync` in `Pages/Episodes/Details.cshtml.cs`. Both paths share one `IOmdbRequestBudget` (a daily `FixedWindowRateLimiter`, default 900/day) so a burst of on-demand episode lookups can't push the combined total over OMDb's real quota and start failing the proactive timer too — if you add a third OMDb call site, it needs to acquire from this same budget.

### Background jobs (Quartz)
Registered in `AddScheduledJobs()` (`InfrastructureServiceCollectionExtensions.cs`), implementations in `Infrastructure/Timers/`:
- `UpdateTvDbInfoTimer` — hourly, refreshes stale series/episode TVDB data
- `UpdateOmdbInfoTimer` — hourly, proactive series OMDb enrichment (see above)
- `MailTimer` — every minute, drains the outbound email queue (`EmailEntity`/`IMailService`)
- `NewEpisodeNotificationTimer` — every 6 hours, raises in-app notifications for newly-aired episodes on tracked series

### Auth
Passwordless only: `IPasswordlessAuthService` emails a single-use magic link (`LoginTokenEntity`); redeeming it issues a standard cookie-auth session with `ClaimTypes.NameIdentifier/Name/Email/Role`. `MarkConsumedAsync` is an atomic `UPDATE ... WHERE ConsumedUtc IS NULL` and the service checks its result — required so two near-simultaneous redemptions of the same link can't both succeed. Resend-cooldown and per-address/site-wide volumetric limits are enforced by `ILoginAbuseGuard`, an in-memory (single-instance-deployment) rate limiter — same file also holds the atomic per-user resend-cooldown check. Roles are just `User`/`Admin` (`UserRole` enum, `Roles` class for the string constants `[Authorize(Roles = ...)]` needs); a user is promoted to Admin by editing the DB row directly, there's no in-app role management UI beyond the `/Admin` dashboard being gated on it.

### Pages worth knowing about
- `Pages/Index` — public, anonymous marketing/landing page (redirects signed-in visitors straight to `/Dashboard`). This is effectively the only page search engines or logged-out visitors ever see; everything else is `[Authorize]`.
- `Pages/Dashboard` — the signed-in home ("your channel"): upcoming episodes, catch-up queue. (Renamed from `Pages/Index` — don't confuse the two.)
- `Pages/Series/Details`, `Pages/Episodes/Details` — the two most complex pages: watch progress, likes, 1–10 ratings (own + community average), TheTVDB/IMDb scores, library tracking, all wired through several services at once.
- `_Layout.cshtml` sets per-page SEO metadata (title/description/OG/canonical/JSON-LD) from `ViewData["Description"]`/`ViewData["Robots"]`, defaulting to `noindex` — since almost everything requires sign-in, indexability is opt-in per page rather than something to remember to lock down on every new authenticated page.

### Data model shape
Everything user-generated hangs off `AppUserEntity` (Guid PK = the `NameIdentifier` claim): `TrackedSeriesEntity` (library), `EpisodeWatchEntity`, `UserLikeEntity` and `UserRatingEntity` (both share the same `{Series, Episode}` target-type-plus-id shape, see `LikeTargetType`/`RatingTargetType`), `NotificationEntity`/`NotifiedEpisodeEntity`, `LoginTokenEntity`, `EmailEntity`. Separately, five `Cached*Entity` tables (`CachedSeriesAggregate`, `CachedSeriesExtended`, `CachedEpisodeExtended`, `CachedSeriesOmdb`, `CachedEpisodeOmdb`) are the Postgres tier of the layered caches described above — not user data, safe to truncate/rebuild from source APIs. `TrackedSeriesEntity.Version` (`uint`) is a Postgres `xmin`-backed optimistic concurrency token; never set it by hand.

### Conventions specific to this repo
- `this.SetSuccessToast(...)` / `SetErrorToast(...)` / `SetInfoToast(...)` (`Extensions/PageModelToastExtensions.cs`, written using C# 14 `extension` block syntax) for all user-facing feedback — never write to `TempData` directly.
- Repositories return domain models, never entities, even internally between service and repository.
- A best-effort external call that shouldn't fail its caller (a translation lookup, an optional series aggregate for a page rendering many series in parallel) has two established helpers to reuse rather than re-inventing: `ITheTvDbService.TryGetSeriesAggregateAsync(...)` (swallow-and-log) and `Task<T?>.AsOptionalAsync(...)` (swallow `TheTvDbApiException` specifically). A *primary* fetch failure should still propagate.
- `EpisodeOrderingExtensions.OrderBySeasonAndEpisode(...)` is the one place "unknown season/episode sorts last, then tie-break by id" is implemented — reuse it rather than re-deriving the sort per call site.
