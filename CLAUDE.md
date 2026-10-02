# CLAUDE.md

Onboarding context for AI sessions working in this repository. Everything below was read from the source on 2026-10-01 (commit `f3e3f57`); claims marked **(inference)** were not seen directly. `AGENTS.md` only points here; this file is the single source of truth. Where it and `README.md` disagree, trust this file and the source.

**Recall.nu** is an ASP.NET Core 10 Razor Pages app for tracking TV series and movies: search TheTVDB, build a library, mark episodes and movies watched, like and rate (1–10), get in-app notifications for newly aired episodes, import an IMDb list export, and see TheTVDB and IMDb (via OMDb) ratings side by side. Sign-in is passwordless (emailed magic link) only.

## Working agreement

Applies to every session.

- Do not create branches, commit, push, stash or rewrite git history. The owner reviews and commits all changes by hand after each session.
- Work in the current working tree on the current branch and leave every change uncommitted.
- Do not change the Docker, compose or port setup (compose files, published ports, nginx assumptions) unless explicitly asked. It works as designed.
- End each session with a summary of the changed files.

## 1. Solution layout

`Recall.sln` contains three projects. There is no `global.json`, `Directory.Packages.props`, `Directory.Build.props` or `.editorconfig`.

| Project | Role | References |
|---|---|---|
| `Recall.Web` (`Microsoft.NET.Sdk.Web`) | The whole application: UI, services, persistence, jobs | none |
| `Recall.Tests` (`Microsoft.NET.Sdk`) | NUnit unit and persistence tests | `Recall.Web` |
| `Recall.Tests.Postgres` (`Microsoft.NET.Sdk`) | NUnit tests that need a real PostgreSQL, started with Testcontainers | `Recall.Web` |

Layering inside `Recall.Web` is by folder and namespace, not by assembly:

| Folder | Contents |
|---|---|
| `Pages/` | Razor Pages, page models, partials and their small view-model classes (`Pages/Shared/*Model.cs`) |
| `Services/` | Business logic, plus the external HTTP clients under `Services/External/{TheTvDb,Omdb}` |
| `Infrastructure/` | EF Core (`Persistence/`), Quartz jobs (`Timers/`), options classes (including `Retention/`), Redis JSON cache, auth helpers, hosting helpers, CSV parser |
| `Domain/` | Plain models: `TheTvDb/`, `Omdb/`, `Internal/` |
| `Mappings/` | Static extension methods for DTO ↔ domain ↔ entity (no AutoMapper) |
| `Extensions/` | DI registration extension methods, toast helpers |
| `Middleware/` | `DevAuthMiddleware` only |
| `Migrations/` | 28 EF Core migrations plus the model snapshot |

## 2. Stack and versions

- **Framework**: `net10.0` in both projects, nullable and implicit usings enabled. No SDK pin; CI uses `10.0.x`. C# 14 features are in use (`extension` blocks in `Extensions/PageModelToastExtensions.cs`, primary constructors everywhere).
- **Packages that matter** (`Recall.Web/Recall.Web.csproj`): EF Core 10.0.12, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, `Microsoft.Extensions.Caching.StackExchangeRedis` 10.0.12, `Quartz` 4.3.0, `Microsoft.Extensions.Http.Resilience` 10.10.0, `Serilog.AspNetCore` 10.0.0 with Console and File sinks.
- Every package referenced by `Recall.Web` is used. (Swashbuckle, the two `NuGet.*` packages and the Visual Studio code-generation package were removed; scaffolding with `dotnet aspnet-codegenerator` would need the last one back.)
- **Tests** (`Recall.Tests/Recall.Tests.csproj`): NUnit 5.0.0, Moq 4.21.0, AwesomeAssertions 9.6.0, `Microsoft.EntityFrameworkCore.Sqlite`, coverlet. `Recall.Tests.Postgres` uses the same NUnit and AwesomeAssertions versions plus `Testcontainers.PostgreSql` 4.15.0. `Microsoft.AspNetCore.Mvc.Testing` drives the pipeline tests.
- **Front end**: no npm, bundler or Tailwind. `wwwroot/lib` is committed and holds only the files that are served: Bootstrap 5.3.3 (`bootstrap.min.css`, `bootstrap.bundle.min.js`), jQuery 3.7.1, jquery-validation (+ unobtrusive), and three things installed with LibMan from `Recall.Web/libman.json`: Phosphor Icons 2.1.2 (regular and fill, `woff2`), and the fonts from `@fontsource` 5.3.0 (Big Shoulders Display 600/800, IBM Plex Sans 400/500/600, IBM Plex Mono 400/500; latin subset, `woff2`). To change a version, edit `libman.json`, run `libman restore` in `Recall.Web` and commit the files. Nothing is loaded from a third party at run time. The theme is four stylesheets in `wwwroot/css` (see "Design system" in section 4). JavaScript is two small files (`wwwroot/js/tvdb-type-filter.js`, `tvdb-cast.js`) plus inline `<script>` blocks in pages.

## 3. Hosting and startup (`Recall.Web/Program.cs`)

**Registration order**

1. `AppCulture.PinToEnglish()` before anything else, then Serilog from configuration (`UseSerilog`, console + rolling daily file).
2. `AddRazorPages`, `AddAntiforgery`, `AddHttpContextAccessor`. There are no MVC controllers and no session state.
3. `AddTrustedForwardedHeaders`: X-Forwarded-For/Proto/Host, applied only when the request comes from a trusted proxy. The `TrustedProxies` section (`Infrastructure/Hosting/TrustedProxyOptions.cs`) lists `Addresses` and `Networks`; with both empty the default is loopback plus the private ranges. An invalid entry fails startup.
4. `AddCookieAuthentication`, `AddAuthorization` (no fallback policy).
5. `AddRedisCache`, `AddPostgres` — both throw at startup if their connection string is missing.
6. TheTVDB (`AddTheTvDb`: `TheTvDbClientState` singleton, typed client with a retry pipeline), `AddOmdb`, `AddApplicationServices`, `AddWeeklyDigest(configuration)`, `AddNotifications`, `AddMail`, `AddWatchlistImport`.
7. Health checks, `AddPasswordlessAuth`, `AddRateLimiting`, `IAppUserRepository`, `AddScheduledJobs(configuration)` (Quartz, plus the retention job's options and repository).

All of these live in `Extensions/ServiceCollectionExtensions.cs` (application services) and `Extensions/InfrastructureServiceCollectionExtensions.cs` (Redis, Postgres, cookies, session, rate limiting, Quartz). Add new integrations there, not inline in `Program.cs`.

**Lifetimes**

| Lifetime | Services |
|---|---|
| Singleton | `TheTvDbClientState`, `IDistributedCacheJson`, `IOmdbRequestBudget`, `ILoginAbuseGuard`, `TimeProvider` (`TimeProvider.System`) |
| Scoped | `AppDbContext`, all repositories, all services, snapshot stores, `ICurrentUserService`, `RecallCookieEvents` |
| Transient (typed `HttpClient`) | `ITheTvDbApiClient`, `IOmdbApiClient`, `ITurnstileVerifier` |
| Factory | `IDbContextFactory<AppDbContext>`, for code that fans out in parallel |

**Options binding**: `Configure<T>(GetSection(T.SectionName))` for `TheTvDbOptions` (`TheTvDb`), `OmdbOptions` (`Omdb`), `MailOptions` (`Mail`), `LoginTokenOptions` (`Login`), `TurnstileOptions` (`Turnstile`). No options validation, except `TrustedProxyOptions` (`TrustedProxies`), which is read and validated eagerly at registration. `RetentionOptions` (`Retention`) is bound the usual way. `AddWeeklyDigest` binds `DigestOptions` (`Digest`) and `SiteOptions` (`Site`) and also reads them eagerly: with `Digest:Enabled` true and `Site:BaseUrl` not an absolute http(s) address, or with `Digest:HourUtc` outside 0–23, startup fails with a message that says what to set.

**Before serving**: `await app.MigrateDatabaseAsync()` applies pending migrations with 10 retries, 3 s apart. Disable with `Database:MigrateOnStartup=false`.

**Middleware pipeline**: developer exception page (Development) or exception handler that logs and redirects to `/Error` + HSTS → `UseForwardedHeaders` → `UseHttpsRedirection` → `UseStatusCodePagesWithReExecute("/Status/{0}")` → `UseStaticFiles` → `UseRouting` → `UseAuthentication` → `DevAuthMiddleware` (Debug builds only) → `UseRateLimiter` → `UseAuthorization` → endpoints. The rate limiter sits after authentication because the `public-details` policy exempts signed-in users. A response that ends with an error status and no body (unknown URL, `NotFound()` from a page, a rejected antiforgery token, a 429) is re-run as `Pages/Status`, which renders inside the layout and keeps the status code; the re-run keeps the original method, so that page has no handlers and ignores antiforgery.

**Endpoints**: `/health` (runs `DbHealthCheck`, a `SELECT 1`), `/health/live` (no checks), `MapStaticAssets`, `MapRazorPages`.

## 4. UI layer

Razor Pages only. No MVC controllers, Blazor or minimal APIs. Two handlers return JSON for inline `fetch` calls: `Series/Details?handler=CheckPriorEpisodes` and `Account/EditProfile?handler=CheckUsername`.

| Page | Access | Purpose |
|---|---|---|
| `/` (`Pages/Index`) | anonymous | Landing page; redirects signed-in users to `/Dashboard` |
| `/Dashboard` | `[Authorize]` | "Continue watching" (the next episode of each series in progress) and upcoming episodes (30 days) |
| `/Search` | `[Authorize]` | TheTVDB series + movie search (GET form) |
| `/Library` | `[Authorize]` | Watching / To Watch (movies) / Up to date / Watched sections; `?section=watching\|dormant\|to-watch\|up-to-date\|watched` shows one section on its own |
| `/Series/Details/{id:int}` | **anonymous read** | Seasons, episodes, progress, like, rating, library toggle |
| `/Episodes/Details/{id:int}` | **anonymous read** | Episode detail, prev/next, watched, like, rating, IMDb score |
| `/Movies/Details/{id:int}` | **anonymous read** | Movie detail, watchlist add/remove, watched, like, rating |
| `/Account/Login`, `/Account/Verify` | anonymous | Request and redeem the magic link |
| `/Account/Logout` | none | Signs out on POST only; a GET just redirects home |
| `/Account/Profile`, `EditProfile`, `Favorites`, `Stats`, `Notifications`, `ImportWatchlist`, `Delete` | `[Authorize]` | Account area. `Stats` is "Your stats" (see "Stats" in section 5). `Delete` is the "Delete my account" confirmation page; see "Deleting an account" in section 5 |
| `/Admin` | `[Authorize(Roles = Roles.Admin)]` | User counts |
| `/Admin/DigestPreview` | `[Authorize(Roles = Roles.Admin)]` | What a weekly digest would say; sends and records nothing. An admin sees their own; in Development any user's, by email |
| `/Digest/Unsubscribe`, `/Digest/OneClick` | anonymous | The digest's unsubscribe link (a page with a button) and its `List-Unsubscribe` address; authorised by a signed token, rate limited per IP |
| `/sitemap.xml` (`Pages/Sitemap`) | anonymous | Dynamic sitemap from the cache tables |
| `/Privacy`, `/Error` | anonymous | `/Privacy` states cookie names, the sign-in link lifetime, retention periods and log retention from code and configuration (`PrivacyModel`), so it cannot drift from them. `/Error` apologises, links home and shows the request id in small print |
| `/Status/{code}` (`Pages/Status`) | anonymous | The 404 page, and the page for any other bodiless error status; reached by re-execution, see section 3 |

- The three Details pages have no `[Authorize]`. Their POST handlers start with `if (!currentUserService.TryGetUserId(out var userId)) return SignInRequired(id, "…");` (`Services/CurrentUserServiceExtensions.cs` plus a small private helper per page), which sends an anonymous caller back with an error toast. All three carry `[EnableRateLimiting(PublicDetailsPolicy)]`: an anonymous client IP gets 60 Details page loads a minute across the three (then 429 with `Retry-After`); signed-in users are not limited. Anonymous requests may fetch an uncached title from TheTVDB but never call OMDb.
- **Layout** (`Pages/Shared/_Layout.cshtml`): nav, footer attributions (TheTVDB and OMDb; do not change their wording or remove them without being asked, the free tiers require attribution), cookie notice, and per-page SEO tags from `ViewData["Title"|"Description"|"Robots"]`. Robots defaults to `noindex, nofollow`; Index, Login, Privacy and the three Details pages opt in. The build number is read from `build.txt` next to the binaries.
- **Partials** (`Pages/Shared/`): `_SeriesCard`, `_CatchUpCard`, `_UpcomingEpisodeCard`, `_FavoriteEpisodeCard`, `_Avatar`, `_EpisodeWatchedToggle`, `_LikeToggle`, `_RatingWidget`, `_TypeFilterBar`, `_EmptyState`, `_TitleHeader`, `_CastList`, `_ConfirmModal`, `_NotificationBell` (injects `INotificationService` and runs an unread `COUNT` on every signed-in page render), `_ToastMessages`. Each takes a small model class from the same folder.
- **Forms**: plain `<form method="post" asp-page-handler="…">`, then redirect (PRG). Antiforgery is the Razor Pages default; inline `fetch` calls copy `__RequestVerificationToken` from the page. State changes are never GET handlers, including sign-out and opening a notification (which marks it read). One page is exempt from antiforgery, `Pages/Digest/OneClick` (`[IgnoreAntiforgeryToken]`): a mail client's one-click unsubscribe POSTs there with no cookie and no form of ours, and the signed token in the URL is the authorisation. Do not add another.
- **Validation**: data annotations on `[BindProperty]` properties (`Login`, `Search`, `EditProfile`) with jQuery unobtrusive validation on the client. Most action handlers take route/form primitives and validate by hand (`if (value is < 1 or > 10)`).
- **Feedback**: `this.SetSuccessToast/SetErrorToast/SetInfoToast(...)` write TempData keys that `_ToastMessages` renders as a dark toast with an icon. A button inside a toast uses `.tvdb-toast-action`. `SetSuccessToastWithWatchedUndo(message, seriesId, batch)` adds an Undo button to the toast: a POST form to `Series/Details?handler=UndoWatched`, shown when the bulk mark inserted more than one row. A one-tap mark with no confirmation whose item then leaves the page (a Dashboard catch-up card) passes `undoSingle: true` and writes through `IWatchProgressService.MarkEpisodeWatchedUndoablyAsync`, so even one episode gets the Undo.

### Design system

The look is one theme: a dark navy canvas ("ink") with the content on a cream panel ("paper"). `ui-review/UI-INVENTORY.md` (not tracked) describes the UI before this system existed.

**Stylesheets**, loaded in this order by `_Layout.cshtml`: `bootstrap.min.css`, the two Phosphor stylesheets, then `css/tokens.css` → `base.css` → `components.css` → `pages.css`.

| File | Holds |
|---|---|
| `tokens.css` | Every custom property: colours, type scale, spacing, radii, shadows, widths, and Bootstrap's root `--bs-*` variables |
| `base.css` | `@font-face`, element defaults, the two surfaces, focus ring, skip link, icon helpers, type helpers, navbar, panel, footer |
| `components.css` | Bootstrap components themed through their `--bs-*` variables, then the shared components (the partials and repeated patterns) |
| `pages.css` | Pieces that belong to one page or one group of pages |

There is no `!important` in the theme and none should be added. Bootstrap is themed through its own variables: root `--bs-*` values in `tokens.css`, per-component variables on the component class (`.btn-primary { --bs-btn-bg: … }`). Plain rules are used only where Bootstrap has no variable (for example `.form-control:focus`). There are no page-level `<style>` blocks.

**Surfaces.** `:root` carries the values for the dark canvas. `.tvdb-shell`, `.modal-content`, `.dropdown-menu` and `.tvdb-surface-paper` re-point the same `--bs-*` variables (text, muted text, links, borders, danger) and the focus ring at the paper palette, so Bootstrap utilities such as `.text-muted` and `.text-danger` are right on both and nothing inside needs to know where it is.

**Colour tokens** (all `--tvdb-*`). Components use tokens only; a new colour is a new token.

| Token | Value | Use |
|---|---|---|
| `ink`, `ink-2`, `ink-3` | `#151b24`, `#1f2733`, `#29333f` | Page background; navbar, footer, media frames; hover on dark |
| `on-ink`, `on-ink-muted`, `on-ink-faint` | `#f1ece2`, 72% and 55% of it | Text on dark |
| `paper`, `paper-dim` | `#f1ece2`, `#e6dfd0` | The panel; recessed rows, tiles, inputs |
| `text`, `text-muted` | `#2c2a24`, `#655b49` | Text on paper (muted is 5.7:1 on paper, 5.0:1 on paper-dim) |
| `ink-line`, `ink-line-strong`, `line` | 12% and 28% ink; 8% white | Borders on paper; borders on dark |
| `signal` | `#e8a33d` | Amber as a fill, and as text or links on dark only |
| `signal-text` | `#8f5410` | Amber as text or a link on paper (5.2:1). Never use `signal` for text on paper |
| `signal-ink`, `on-signal` | `#6b3d0f`, `#2a1a05` | Text on an amber tint; text on a solid amber fill |
| `ok`, `ok-ink`, `on-ok` | `#6f9d6f`, `#2f4a2f`, `#1c2b1c` | Watched, continuing, success |
| `danger`, `danger-ink` | `#a3472f`, `#6b2c1f` | Remove, errors |
| `info`, `info-ink` | `#4f8a93`, `#234047` | Movies, informational |
| `like` | `#d9536a` | The heart |
| `*-tint`, `*-edge` | accent at low alpha | Fill and border of chips, alerts and soft buttons |
| `focus` | `signal` on dark, `ink` on paper | The focus ring (`:focus-visible`, 2px, offset 2px) |

**Type.** The root font size is 16px at every width. Three families, self-hosted:

| Token | Family | Use |
|---|---|---|
| `font-display` | Big Shoulders Display 600/800 | Headings and big numbers |
| `font-ui` | IBM Plex Sans 400/500/600 | Everything people read or press: body text, buttons, navigation, labels, forms |
| `font-mono` | IBM Plex Mono 400/500 | Data only: episode codes (`S05 · E07`, `E1`), countdowns, counts and ids. Apply with `.tvdb-mono` |

Seven sizes, used through the tokens, never as literals (an eighth, `text-4xl` at 56px, exists for the landing page's headline and nothing else):

| Token | Size | Use |
|---|---|---|
| `text-xs` | 12px | Fine print, badges, eyebrows |
| `text-sm` | 14px | Secondary text, buttons, labels |
| `text-md` | 16px | Body |
| `text-lg` | 18px | Lead text, `h3` |
| `text-xl` | 24px | `h2` |
| `text-2xl` | 32px | Stat numbers; `h1` on phones |
| `text-3xl` | 40px | `h1` on desktop (`h1` is `clamp(2xl, 5vw, 3xl)`) |

Helpers: `.tvdb-eyebrow` (small uppercase label), `.tvdb-prose` (running text), `.tvdb-meta` (secondary line: dates, runtimes, hints), `.tvdb-mono` (data). Line heights `leading-tight` 1.1, `leading-snug` 1.3, `leading-body` 1.55.

**Spacing and radius.** `space-1` … `space-7` are 4, 8, 12, 16, 24, 32 and 48px. `radius-sm` 4px (chips, code), `radius-md` 8px (buttons, inputs, posters, tiles), `radius-lg` 12px (the panel, cards, modals), `radius-pill`, `radius-round`.

**Icons** are Phosphor, regular and fill weights, and every icon is a constant in `Infrastructure/Display/Icons.cs` holding the full class (`Icons.Series` is `"ph ph-television-simple"`, `Icons.Liked` is `"ph-fill ph-heart"`). Markup writes `<i class="@Icons.Series" aria-hidden="true"></i>`; C# that picks an icon returns the constant. Do not write `ph-…` literals in pages, use Unicode characters or inline SVG as icons, or use Bootstrap's built-in icons (`btn-close`, `navbar-toggler-icon`; the close button is `.tvdb-close` with `Icons.Close`). A new icon is a new constant; `IconsTests` fails if a constant names an icon Phosphor does not have. Icons are decorative (`aria-hidden`); the text or the control's `aria-label` carries the meaning. Size helpers: `.tvdb-icon--sm|md|lg|xl`, and `.tvdb-icon--fw` for a fixed-width box in menus and lists. The one icon drawn from CSS is the external-link arrow after `.tvdb-metalink[target=_blank]`.

**Page frame.** Every page puts its content in one `<div class="tvdb-shell">`. Two widths only: the full panel (1100px) for grids and detail pages, and `tvdb-shell--narrow` (680px of content) for forms and text pages; do not narrow content with grid columns. Every page has exactly one `h1` and heading levels do not skip (`h2` sections, `h3` inside them). A way back is `<a class="tvdb-back-link">` with `Icons.Back`, always the first thing inside the panel. The layout provides the skip link (`#main`), marks the current navigation item with `aria-current="page"` (`CurrentIf(...)` in `_Layout.cshtml`), and the mobile menu is `#mainNav`.

**States.** "Nothing here" is the `_EmptyState` partial (`EmptyStateModel`: icon, heading, sentence, an optional button and an optional quieter second one, heading level; `Inline = true` inside a section, including an empty section of a page that has other content). It is also the body of the status, error, sign-in-link and not-found pages, with `HeadingLevel = 1`. Do not use an alert or a line of muted text for an empty page. A detail page that cannot load its title renders the same partial itself and sets the status code: 503 when TheTVDB failed, 500 for anything else, and (Episode Details) 404 for an episode that does not exist, with a link back to the series the visitor came from. `NotFound()` from a page model still goes to `Pages/Status`.

**Links.** A link in running text keeps its underline (Bootstrap's default). A link that stands on its own in a list or a card (an episode title, a person's name, a card title) is `.tvdb-link-plain`: no underline until hover or focus. `.tvdb-metalink` is the quiet underlined link inside a value or a source line.

**Actions.** A page has one amber `btn-primary`: the next thing to do ("Mark S05E07 watched", "Add to library", "Mark as watched"). A toggle that is on is `btn-watch--on` ("Watched Fri, Sep 11"); a state that is not a button is `.tvdb-title-state` ("Up to date", "On your watchlist"). Secondary actions are `btn-outline-dark`. Removing something is never in the action row: it is a `.tvdb-quiet-action` text button further down that opens the `_ConfirmModal` partial (`ConfirmModalModel`), whose confirm button is a `btn-danger`. The only other `btn-danger` is the final button of "Delete my account". A quiet action that destroys the account itself is `.tvdb-quiet-action--danger`. Do not use the browser's `confirm()`. Rarely used actions on a list go in a `.tvdb-icon-button` (`Icons.More`) dropdown. Interactive targets are at least 36px. Button sizes go by role: a page's actions (primary and secondary) are the default size; an action that belongs to a section or to one row of a list ("Mark all as read", "Mark read", "View import", "Show all N", the filter pills) is `btn-sm`; the landing page's "Get started" is the one `btn-lg`.

**Rhythm.** Sections of a page (an `h2` and what belongs to it) are `space-6` apart: wrap them in `.tvdb-page-section`, or on a text page put `.tvdb-text-page` on the panel. An `h2` sits `space-3` above its content (set on `h2` itself; do not add `mb-*` to one). An `h1` is `mb-4` above the page, or `mb-2` above a lead sentence that then carries the `mb-4`. The Details pages' ruled `.tvdb-section` and the landing page keep their own, larger steps.

**Titles in lists and on cards** have two styles and no others, defined once in `components.css`: a row's title (episode, search result, import row, notification) is the UI face, semibold, body size; a card's title (poster card, upcoming card, liked episode, person) is the same a step smaller. A read notification is the one exception: regular weight, because bold is its unread signal. A new list or card adds its title class to one of those two rules.

**Detail pages** (Series, Movie) share `_TitleHeader` (`TitleHeaderModel`, built by the page model's `BuildHeader`): poster, title, a one-line summary from `TitleSummary` ("2008–2013 · Ended · 5 seasons", "2023 · 3h 9m"), genres, the action row and the rating; a visitor gets a sign-in card instead of actions. The page wraps it in `.tvdb-title` and adds `.tvdb-title__details` (facts in the order dates, last watched, IMDb rating, Recall rating; overview; awards; quieter production facts; the quiet remove button). On a phone the poster is a thumbnail beside the title. Series order is header, details, episodes, cast. People are `_CastList` (`CastListModel`), built by `CastBuilder`: cast and crew separately, one entry per person with characters or jobs joined, initials when there is no photo, the name linking to TheTVDB; both collapsed until "Show all N" is pressed (`js/tvdb-cast.js` shows the button only when something is cut off, and makes the cut-off people inert). The cast is portrait cards: exactly one full row on wider screens, a sideways-scrolling row on phones sized so the next card peeks in. The crew (`Compact = true`) is a list of name and jobs beside a 40px round photo or initials, in one, two or three columns. Every detail page ends with a `.tvdb-source-line` (TheTVDB id, IMDb id); ids are not shown anywhere else.

**Dashboard.** Three small linked stats in one row at every width (`.tvdb-stat-row` of `.tvdb-stat-item`; the same compact style, without links, is Admin's). The first section is "Continue watching" (the `_CatchUpCard` partial and `CatchUpItem` keep their older names): the active series in the order of `ContinueWatchingOrder`, and under them one quiet link, "N series you haven't watched in a while", to the Library's dormant group (no link when N is 0; those series get no card). The "Unwatched episodes" stat still counts every series. A card's image is the episode's still, else the series' background art (`SeriesAggregate.BackgroundUrl`), else a dark placeholder; never the poster, which is the wrong shape. The caption's shade sits only behind the text and stops short of black. Upcoming cards are compact, in two columns from 768px, in the order poster, series and episode, then the date with a small countdown as a secondary line (right-aligned on wide cards; on phones the last line of one text column); under a "Today" or "Tomorrow" heading the card shows neither (`UpcomingEpisodeCardModel.ShowDate`).

**Poster cards** (`_SeriesCard`, `SeriesCardModel`): no type badge on the poster. The meta line under the title says "Series · 2008" or "Movie · 1995", or "Movie · watched Sep 25" when the card passes a `Caption`; a series under Watching also gets `ProgressText` ("6 of 16 · S05", from `SeriesWatchProgress.CurrentSeason`), with the bar on the poster as the glance. The heart on a poster is 36px. The grid is two columns on phones. A page heading with one action uses `.tvdb-page-head` (action right-aligned on the same line); a filter bar goes on its own line below in `.tvdb-filter-row`.

**Navigation on phones.** The collapsed menu has no dropdown: a `.nav-identity` row (avatar beside name and email), then Profile, Favorites, Stats, Notifications, Admin panel and Sign out as plain rows (`d-sm-none`). The avatar dropdown is for 576px and up (`d-none d-sm-block`). A link added to one needs adding to the other.

**Words.** The interface says "Sign in" and "Sign out" everywhere (navbar, menus, Profile, the sign-in page); not "Login" or "Logout", which survive only in page and handler names.

**Library sections.** `LibraryModel.Sections` is the sections in page order, each with a slug (the dormant group is one of them, drawn inside Watching on the full page). On the full page Watching is always the two-column (and wider) grid; below 576px the other three are one sideways-scrolling row each (`.tvdb-series-grid--row-on-phone`, sized so the next poster peeks in, like the cast row) with a "See all N" link to `/Library?section=<slug>`: the same page showing that section alone, with its own `h1` and a back link. From 576px up the full page shows every section as a grid and the link is not rendered visibly. A like posted from the one-section view returns to it.

**Landing.** A hero (`.tvdb-landing-hero`: headline at `text-4xl`, one sentence that names series and movies, one "Get started" button; the navbar has the only other way in), a product preview, three features with icons, and "How it works" (`.tvdb-steps`, also used on Import). The preview (`.tvdb-landing-preview`) is the app's own component classes as static markup: no links or buttons, `aria-hidden`, `pointer-events: none`. Its artwork is abstract (`.tvdb-art` with the `--tvdb-art-*` tokens): TheTVDB's images are licensed for display with its metadata, not for marketing, so never put a real poster or still on the landing page.

**Forms.** The field comes first; explanation that is not needed to fill it in goes below the form (the sign-in page's benefits list). A field has one message area (`.tvdb-field-message`): a server-side message and a live status line never show together, and the hint is a line of its own under it (Edit profile). Buttons sit in `.tvdb-form-actions`: one row, Cancel first, the saving action last, on the right. Validation messages are written for people ("Enter a valid email address."), set with `ErrorMessage` on the attribute. After a sign-in link is requested the page shows a state of its own: envelope icon, "Check your inbox", the address, the link's real lifetime (`LoginTokenOptions.TokenLifetimeMinutes`) and "Use a different email".

**Cookies.** There is no cookie notice and no consent prompt: everything the app stores in a browser is strictly necessary, and the Privacy page is the one place that explains it. What is stored: the sign-in cookie (`Recall.Auth`, 30 days sliding, set at sign-in), the antiforgery cookie (session; set on pages that render a form, which for a visitor is the sign-in page and the three Details pages, not the landing or Privacy page), and the TempData cookie (only between a POST and the page that shows its toast). Nothing is kept in local or session storage (the layout still removes the old `recall.cookie-notice-dismissed` key from browsers that have it; that line can go after a while). Third-party requests: posters and stills from `artworks.thetvdb.com`, and Cloudflare Turnstile on the sign-in page when its keys are set. Emails load nothing from anywhere (no images, no tracking). Anything new that is stored in a browser, or loaded from another host, needs a line on the Privacy page, and anything that is not strictly necessary would need a consent prompt first.

**Avatar.** There are no profile pictures. The signed-in user's avatar is the `_Avatar` partial (`AvatarModel(name, size)`): initials from `Initials.Of(name)` on amber, in three sizes (`sm` navbar, `md` phone menu, `lg` profile). It is decorative; the name beside it or the control's `aria-label` carries the meaning. Cast and crew without a photo use the same `Initials.Of`.

**Status words.** A stored status is never printed as its enum name. `ImportStatusDisplay.For(status)` is the one mapping for import rows (label, badge colour, icon): Imported is ok, Already had it and Unsupported neutral, No match amber, Failed danger, Waiting neutral. A new status enum shown to people gets a mapping like it, and a test that every value has one.

**Lists of results** (Search, Import, Notifications) are lists, not tables: a row is a title, a muted meta line ("Movie · 1995", "Movie · rated 8"), an optional badge, and detail text beneath, so nothing scrolls sideways at any width. Search shows the first 20 results and reveals the rest (already on the page, `hidden`) 20 at a time with "Show more". A result's title is an `h2` in the UI face at body size, like every other list title, not the display face; overview lines are capped at 70ch. The search field is wide and its button keeps its natural width beside it at every width. An overview is English when TheTVDB has one; otherwise the original, marked with its `lang` (`SearchResultItem.OverviewLanguage`, from `SearchResultMappings.ToLanguageTag`). Results the user already has carry an "In library" or "Watched" badge (`SearchModel.LibraryBadge`).

**Profile** is full panel width: a header (avatar, name and email once, and the watch-time figure: `WatchTimeSummary.Readable`, e.g. "1 month, 3 days", "across 815 episodes and 3 movies", as a link to Stats; it is the Stats page's own total, from `IStatsService`), then `h2` sections: Favorites (one `.tvdb-poster-row` of posters and a "See all" link in a `.tvdb-section-head`), Import (one sentence, `ProfileModel.ImportSummary`), Account (facts, with "Sign out" and "Delete my account" as quiet actions at the bottom).

**Notifications.** A row is the series poster thumbnail, the title, the text, and a foot line with the age and "Open episode". The row itself is the button that opens the episode (a POST, since it marks the notification read); beside it an unread row has a small "Mark read" button (`OnPostMarkReadAsync`). Unread is shown one way only: a dot and a bold title.

**Stats** (`Pages/Account/Stats`) is full panel width: a lead sentence, four totals (`.tvdb-stat-row--four`, two by two on a phone), then `h2` sections: Month by month, Most watched series, Genres, Your ratings. Charts are drawn by the server as lists, with no script and no chart library: twelve columns (`.tvdb-month-chart`) and ranked rows with a bar (`.tvdb-bar-list`). A bar's length is `--tvdb-bar`, a percentage set inline on the element; that is data, not a design value, and the one inline style the theme allows. Every chart says its figures in text: a month column carries a visually hidden sentence ("October 2026: 12 episodes, 1 movie, 9 h 40 min"), a ranked row shows its name and value, and the bars themselves are `aria-hidden`; the monthly figures are also a table behind a native `<details>`. On a phone a month's label is one letter. Amounts are written by `StatsFormat` ("9 h 40 min", "312 h"), months by `DisplayDate.Month` / `MonthShort`. States: a user with nothing gets the page-level `_EmptyState`; one whose history is all bulk-marked or imported gets totals and top lists with an inline `_EmptyState` where the chart would be; no ratings is an inline `_EmptyState`; the Genres section is left out when no title has genres.

**A page that waits for a background job** (Import while it is matching) shows a progress bar with the count in words ("12 of 40 rows matched") and reloads itself with a `setTimeout` every `RefreshSeconds`; do not use `<meta http-equiv="refresh">`, which axe reports.

**Dates** are formatted only by `Infrastructure/Display/DisplayDate.cs`: `DisplayDate.Format(date, today)` gives "Sat, Sep 4" for a date in the current year and "Sep 4, 2026" otherwise; it also takes the date strings TheTVDB sends. `DisplayDate.Short` is the same without the weekday ("Sep 4"), for a tight spot such as a poster card's meta line. `today` is the page model's `Today` (from `TimeProvider`). `DisplayDate.Time` gives a time of day ("8:00 PM"). `DisplayDate.Month` and `MonthShort` give a calendar month ("October 2026", "Oct") for the Stats chart. `DisplayDate.Relative` ("3h ago") is for notifications only. Machine-readable output (sitemap, JSON-LD) keeps ISO dates and does not use it.

**Logo.** `wwwroot/images/logo.svg` (navbar lockup, cream wordmark), `logo-light.svg` (ink wordmark), `logo-mark.svg`, plus `favicon.svg`, `favicon.ico`, `apple-touch-icon.png` and `images/og-image.png`. All are generated by `ui-review/logo/render.sh [bookmark|play|check]` from `make_logo.py`, which draws the mark and converts the wordmark to paths; edit the script, not the files. The navbar logo is sized by height (32px, 28px on phones). The mark is the bookmark variant (the script's default). The scripts need the Python packages in `ui-review/logo/requirements.txt`.

## 5. Domain model

All user data hangs off `AppUserEntity` (Guid PK, equal to the `NameIdentifier` claim), with cascade delete.

| Entity (table) | Meaning | Unique key |
|---|---|---|
| `AppUserEntity` (`app_user`) | Username, email, `Role` (`User`/`Admin`, stored as string), `digest_opted_in_utc` (null = weekly email off; the value is the record of consent) and `digest_prompt_dismissed_utc` (the Dashboard's one-time offer was declined) | email; username |
| `TrackedSeriesEntity` (`tracked_series`) | A series in the user's library, with denormalized name/overview/image/first-aired. `Version` is an `xmin` concurrency token; never set it by hand | (user, tvdb id) |
| `EpisodeWatchEntity` (`episode_watch`) | One watched episode, with `WatchedUtc` (when it was marked) and `source` (how: see "Watch source" below) | (user, episode) |
| `TrackedMovieEntity` (`tracked_movie`) | A movie on the user's watchlist ("want to watch"), with the title as known when added. No `xmin` token, unlike `tracked_series` | (user, tvdb id) |
| `UserMovieWatchEntity` (`user_movie_watch`) | One watched movie, with `WatchedUtc` and `source` | (user, movie) |
| `UserLikeEntity` (`user_like`) | Like on a `Series`, `Episode` or `Movie` (`LikeTargetType`) | (user, type, target) |
| `UserRatingEntity` (`user_rating`) | 1–10 rating, same target shape, DB check constraint | (user, type, target) |
| `NotificationEntity` (`notification`) | In-app notification (only type: `NewEpisode`) | — |
| `NotifiedEpisodeEntity` (`notified_episode`) | Ledger making new-episode notifications idempotent | (user, episode) |
| `LoginTokenEntity` (`login_token`) | SHA-256 hash of a magic-link token, expiry, consumed time | token hash |
| `EmailEntity` (`email`) | Outbound mail queue. `body` and `html_body` are erased when a message is delivered (`MarkSentAsync`) and when it exhausts `Mail:MaxSendAttempts` (`RecordFailedAttemptAsync`), so only a still-pending row holds content. `priority` orders the queue (lower first: sign-in links 0, digests 10). `list_unsubscribe_url`, when set, becomes the `List-Unsubscribe` headers; it is erased with the bodies | — |
| `DigestSendEntity` (`digest_send`) | Ledger of the weekly digest: one row per user and week (`period_start`, the scheduled send date), `Queued` or `Skipped` | (user, period start) |
| `WatchlistImportJobEntity` / `WatchlistImportItemEntity` | IMDb CSV import job and its rows | — |
| `Cached*Entity` (7 tables) | Postgres tier of the metadata caches; `jsonb` payload. Not user data, safe to rebuild | tvdb id (+ language for aggregates) |

**User-owned tables.** These hold rows that belong to one user: `tracked_series`, `tracked_movie`, `episode_watch`, `user_movie_watch`, `user_like`, `user_rating`, `notification`, `notified_episode`, `login_token`, `digest_send`, `watchlist_import_job` and (through its job) `watchlist_import_item`, plus the rows of `email` addressed to the user. The digest preference and the dismissed offer are columns of `app_user` and go with the row. The `Cached*` tables are shared metadata and belong to nobody.

**Deleting an account.** `Pages/Account/Delete` ("Delete my account", reached from the bottom of Profile's Account section) explains what goes and that it cannot be undone, and enables its final button once the username (or email address) is typed; the POST checks that again. `IAppUserRepository.DeleteAccountAsync` then deletes every user-owned table above and the user row in one serializable transaction, and refuses (`AccountDeletionResult.OnlyAdmin`) when the user is the only admin; the page shows an explanation instead of the form in that case. Afterwards the handler signs the browser out, sets a success toast and redirects to the landing page. Other sessions of the account stop at their next cookie revalidation (at most 5 minutes, `RecallCookieEvents`), since the user row is gone; until then a write from such a session fails on the foreign key. Recall ratings are computed from `user_rating` on each read, so they change at once. Signing in again with the same email creates a new, empty account. Database backups (about 9 days) and server logs keep deleted data until they expire; the Privacy page says so.

**A new user-owned table must be added to `DeleteAccountAsync`** (and to the list above). `AccountDeletionTests` in `Recall.Tests.Postgres` asks the database for every table with a `user_id` column and fails if any still has rows of a deleted user, or if the test does not seed it.

**Weekly digest** (`Services/Digest/`). Opt-in: nothing is sent to a user whose `digest_opted_in_utc` is null. It is switched on from Profile's "Weekly email" row or from the Dashboard's one-time offer ("Turn it on" / "No thanks", both POST; the offer shows until either is answered), and off from Profile or the link in any digest. **It is live in production**: `.env.prod` sets `Digest__Enabled=true`, `Digest__MaxPerRun=50` and `Site__BaseUrl`. The default in `appsettings.json` is still off, so a local or test instance sends nothing and hides the switch and the offer unless it sets `Digest:Enabled` itself.

- **Content** (`DigestBuilder`, pure): three sections over 7-day UTC windows. New seasons (a regular season's first episode aired in the past week and is unwatched; any tracked series, dormant ones included), Ready to watch (unwatched regular episodes aired in the past week, for series in the main continue-watching list, in that order, minus those already under New seasons), Coming up (regular episodes airing in the next week, for every tracked series that is not dormant). Specials are never mentioned; dormancy is `ContinueWatchingOrder.Arrange`'s call. Ten lines per section, then "and N more". Nothing to say means no email.
- **Email** (`DigestEmailRenderer`): HTML and plain text. No images, no tracking pixel, no redirect links; "Metadata provided by TheTVDB" and the unsubscribe link in both parts. The HTML is table layout with inline styles and its own literal colours and `!important` in a dark-mode block: mail clients need that, and the theme's rules about tokens do not apply inside an email.
- **Links** are absolute, built from `Site:BaseUrl`, since the job has no request.
- **Unsubscribe**: the token (`DigestUnsubscribeTokens`) is the user id protected with ASP.NET Data Protection under its own purpose; it does not expire and depends on the persisted key ring. The link in the body opens `/Digest/Unsubscribe`, a page with a button that POSTs (a GET changes nothing, so mail scanners cannot unsubscribe anyone). The `List-Unsubscribe` header points at `/Digest/OneClick` with `List-Unsubscribe-Post: List-Unsubscribe=One-Click`. Both answer the same whether or not the account exists.
- **Sending** (`WeeklyDigestService`, run hourly by `WeeklyDigestTimer`): `DigestSchedule.DuePeriod` says which week is due (the configured day and UTC hour, for `Digest:CatchUpHours` afterwards). Up to `Digest:MaxPerRun` opted-in users without a ledger row for that week are handled per run. `IDigestRepository.RecordAsync` writes the ledger row and the queued email in one `SaveChanges`, and returns false on the unique index, so nobody gets a week twice. A week with nothing to say is recorded as `Skipped`. A user whose digest fails gets no ledger row and is retried next hour; an email that fails to send is retried by the mail queue and never re-queued.
- **API cost**: none. `DigestComposer` reads series through `ITheTvDbService.GetCachedSeriesAggregateAsync` (Redis, then the Postgres snapshot, never the API) and skips a series that is not cached; one run reads each series once.
- **Preview**: `/Admin/DigestPreview` composes a digest for a date without sending or recording. Outside Development an admin can only preview their own.

**There are no series, season, episode or movie tables.** Metadata exists only as TheTVDB ids on user rows plus JSON snapshots in the cache tables, deserialized into `Domain/TheTvDb` records (`SeriesAggregate`, `Series`, `Episode`, `MovieAggregate`).

**"Watching" vs "watched" is derived, never stored.** `LibraryModel.ClassifyTrackedSeriesAsync` computes it per request, with the section of a series decided by `SeriesLibraryStateRule.Of` (which Stats also uses for "series finished"):

- **Watching**: tracked series with at least one aired regular episode not marked watched.
- **Up to date**: no aired regular episode unwatched and TheTVDB status is not "Ended".
- **To watch**: movies on the watchlist (`tracked_movie`), most recently added first.
- **Watched**: no aired regular episode unwatched and status is "Ended"; watched movies are listed here too.

"Regular" means not a special: see the specials rule below.

**Progress** is computed by `Services/WatchTracking/WatchProgressCalculator.Build`: order episodes in *watch order* (`OrderByWatchOrder`: numbered seasons, then the specials, then anything without a season; tie-break by id), keep the regular episodes aired on or before today, and the next episode to watch is the first of those without an `EpisodeWatch` row. Episodes flagged `IsMovie` are excluded. `CurrentSeason` is the same count within the next episode's season. The episode list always comes from the series aggregate.

**"Continue watching": order and grouping** are decided in one place, `Services/WatchTracking/ContinueWatchingOrder`, for the Dashboard's Continue watching cards and the Library's Watching section (at every width). Nothing else sorts or splits those lists, and the Library's other sections stay alphabetical.

- **Order** (`Order`): series with watch activity come first, most recently watched first; then series with nothing watched yet, most recently added to the library first (`tracked_series.created_utc`); ties by name, then id. Activity is the latest `WatchedUtc` of any of the user's episode watches for the series, specials included: a special never drives the next episode, but watching one counts as watching the series. It comes from `IEpisodeWatchRepository.GetLastWatchedUtcBySeriesAsync(userId)`: one `GROUP BY` query per page load for all of the user's series, with no cache.
- **Grouping** (`Arrange`, which returns the main list and the dormant group, each ordered): a series in the queue is dormant, "haven't watched in a while", when its last activity is more than `Library:DormantAfterDays` days ago (default 90; counted in UTC dates), or, for a series never started, when it was added that long ago. 0 or less turns the feature off. A series with no known date is never dormant.
- **Coming back**: watching anything brings a series back by itself. A dormant series also returns to the main list while the first episode of one of its regular seasons aired within `Library:PremiereReturnDays` days (default 14; `HasRecentPremiere`). It is placed after the series with real activity and before those not started yet. An ordinary episode does not bring a series back (an abandoned weekly show airs all the time), and neither does a special.
- **Where it shows**: the Dashboard lists only the main list and links to the rest. In the Library, Watching and its count are the main list; the dormant series are a sub-group inside the Watching section ("Haven't watched in a while N", an `h3` in a `.tvdb-subgroup`): collapsed behind a "Show N" button from 576px, a sideways-scrolling row with "See all N" on phones, and `/Library?section=dormant` on its own.

The settings are `LibraryOptions` (`Library` section), bound with `BindConfiguration` in `AddApplicationServices`.

**Specials (season 0) never count toward progress** (`WatchableEpisode.IsSpecial`). They are in the ordered episode list, shown last on their own "Specials" tab, and can be marked watched there, but `WatchProgressCalculator.Build` leaves them out of everything it derives: they are not in the released, watched or unwatched counts, and a special is never the next episode. So a series whose regular episodes are all watched is up to date (Series Details header, Library section, Dashboard) whatever specials remain, and a special is never a catch-up card. "Earlier" is judged within the same kind (`CountPriorUnwatched`, `IdsThrough`): catching up to S02E03 never marks a special, and marking a special never marks the series. `WatchProgressCalculator.DefaultSeason` picks the season a series page opens on: the next episode's season, else the latest numbered season, and the specials only when the series has no other season. An episode with no season number is not a special. Not covered by this rule: Upcoming on the Dashboard and new-episode notifications still include a special that airs.

**Watch writes go through `IWatchProgressService`**, never straight to `IEpisodeWatchRepository`: `MarkEpisodeWatchedAsync` / `MarkEpisodeWatchedUndoablyAsync` / `ToggleEpisodeWatchedAsync` first verify the episode belongs to the submitted series (the aggregate, falling back to the episode's own record) and reject a future air date. `MarkWatchedThroughAsync` marks the target and every earlier episode of the same kind (see the specials rule above), skipping any with a future air date.

**Watch source.** `WatchedUtc` is when a watch was *marked*, which is not always when the title was watched, so every watch row also records how it was written (`WatchSource`, stored as its name in `source`):

| Source | Written by | Its date counts as a watch date |
|---|---|---|
| `Single` | One episode or movie marked on its own: the toggles, the Dashboard's one-tap mark, "Mark as watched" on a movie, and the episode the user clicked in "mark this and earlier" | yes |
| `Bulk` | "Mark season watched" (every row, even when one is left), and the earlier episodes swept in by "mark this and earlier" | no |
| `Import` | A movie marked watched by the IMDb import | no |
| `Unknown` | Rows from before the column existed (2026-10-02) that the migration did not recognise as either of the above; also the column's default | yes |

`WatchSourceExtensions.IsDated` is that last column. The `WatchSource` migration classified the existing rows once: a movie watch within a minute of one of the user's import rows that resolved to it became `Import`; three or more episode watches by one user in one series, each within two minutes of the one before, became `Bulk`; everything else stayed `Unknown`. The source is set by the write paths (`IEpisodeWatchRepository.MarkWatchedAsync` is always `Single`; `MarkWatchedRangeAsync` takes the source and, optionally, the one clicked episode that is `Single` regardless; `IMovieWatchRepository.ToggleAsync` and `IMovieTrackingService.MarkWatchedAsync` take it). A new way of marking something watched must pass the right one. A batch still shares one `WatchedUtc`, so the undo is unaffected.

**Stats** (`Services/Stats/`, page `Pages/Account/Stats`). `IStatsService.GetAsync(userId)` returns a `UserStats` record, computed by the pure `StatsBuilder.Build(input, today, window)`; Profile's watch-time figure is its `Totals.WatchTime`, so the two cannot disagree.

- **Two rules.** Totals and the top lists count *every* watch row. The monthly chart counts only rows whose source `IsDated`, in the UTC month of `WatchedUtc`; `StatsUndated` says how many were left off, and the page says so under the chart.
- **Totals**: watch time, episodes (every `episode_watch` row, specials and movie-flagged entries included: time is time), movies (every `user_movie_watch` row), and series finished: tracked series that `SeriesLibraryStateRule` calls `Finished` and that the user has watched at least one regular episode of.
- **Runtime**: an episode's own, else the series' average (also for a watched episode the aggregate no longer lists), else nothing; a movie's own. OMDb's runtime is never used (it is unreliable).
- **Month by month**: the current UTC month and the eleven before it (`StatsWindow.LastTwelveMonths`; `StatsWindow.CalendarYear` exists for a year-in-review page nobody has built). Totals are all-time whatever the window.
- **Most watched series**: top 5 by watch time, then episodes, then name; a series without a cached record has no name and is not listed.
- **Genres**: TheTVDB's, for series and movies alike; top 6 by watch time. A title counts in full under each of its genres, so they add up to more than the total. `StatsGenreCoverage` says how many watched titles have genres at all (see "Genres" under TheTVDB in section 7), and the page says "Based on N of M titles" until that is all of them.
- **Ratings**: how many titles got each value 1–10 (series, episodes and movies together), the count and the average, from `IRatingRepository.GetValueCountsAsync`.
- **No streaks.** Bulk marking would break them for people who watch every week and catch up their marks monthly. Decided 2026-10-02.
- **Zero API calls.** Metadata is read with `GetCachedSeriesAggregateAsync` / `GetCachedMovieAggregateAsync` only (Redis, then the Postgres snapshot), at most 16 reads at a time. A title in neither is still counted, with no length, name or genre, and `StatsGaps` reports it; the page ends with one sentence about what was counted without a length. A watched series is always cached (marking needs the aggregate). An imported movie is cached only once the Library or its own page has been opened: the importer matches through TheTVDB's remote-id search, which returns a base record, and caching the full movie would cost two more requests per row, so it does not.
- **No per-user cache.** Measured 2026-10-02 on a review clone (Debug build, 945 episode watches in 18 titles): about 10 ms; with 500 watched movies added, about 70 ms the first time (Redis cold, 500 snapshot reads) and 21–29 ms after. The threshold for adding a Redis cache was 100 ms. If one is ever added, key it on a fingerprint of the user's rows and remove the key in `DeleteAccountAsync`.
- **Nothing new to delete.** Stats adds no table; `source` is a column on two tables account deletion already empties.

**Bulk marks and undo.** `MarkSeasonWatchedAsync` / `MarkSeasonUnwatchedAsync` act on one season of the aggregate. Every row a bulk mark inserts (`EpisodeWatchRepository.MarkWatchedRangeAsync`) shares one `WatchedUtc`, truncated to the millisecond and returned as a `WatchedBatch`; `UndoWatchedBatchAsync` deletes exactly the user's rows in that series with that timestamp. Keep the truncation: Postgres stores microseconds, so an untruncated .NET timestamp would not compare equal after a round trip (SQLite tests cannot show this; it was verified by hand against Postgres).

**Movies** are on the watchlist (`TrackedMovie`) or watched (`UserMovieWatch`), never both, and independently liked or rated. `IMovieTrackingService` (`Services/WatchTracking/MovieTrackingService.cs`) owns that rule: marking a movie watched takes it off the watchlist, a watched movie can't be added, and un-watching does not put it back. Page models and the importer call the service, not the two repositories, for writes.

## 6. Data access

- **ORM**: EF Core 10 on PostgreSQL via Npgsql. One context, `Infrastructure/Persistence/AppDbContext.cs`. No Dapper or raw SQL.
- **Configuration**: one `IEntityTypeConfiguration<T>` per entity in `Persistence/Configurations/`, applied with `ApplyConfigurationsFromAssembly`. Tables are singular snake_case, columns snake_case, enums stored as strings, timestamps `timestamp with time zone`.
- **Context options** (`AddPostgres`): shared `NpgsqlDataSource` with `EnableDynamicJson()`, `SplitQuery` by default, and `MultipleCollectionIncludeWarning` raised to an exception.
- **Audit timestamps**: an entity opts in by implementing `IHasAuditTimestamps` (`CreatedUtc`, `UpdatedUtc`); `SaveChanges`/`SaveChangesAsync` stamp every tracked one. Ten entities do. `ExecuteUpdateAsync` and raw SQL bypass the change tracker, so those set `UpdatedUtc` themselves. `AuditTimestampTests` fails if an entity has both columns but not the interface.
- **Two ways to get a context**:
  - Repositories and `EpisodeOmdbSnapshotStore` inject the scoped `AppDbContext`. Calls on it must stay sequential.
  - `TvdbSnapshotStore`, `OmdbSnapshotStore`, `MovieOmdbSnapshotStore` and `SitemapService` use `IDbContextFactory` and open a context per call, so callers may run them under `Task.WhenAll`.
- **Repositories** (`Persistence/Repositories/`): interface + sealed implementation, return domain models or small records, reads use `AsNoTracking`. Exception: `IAppUserRepository` returns `AppUserEntity`. No page model touches `AppDbContext`. An expected outcome is a return value (`AddAsync` returns `false` for "already there"), not an exception for the caller to pattern-match.
- **Concurrency idiom**: check, insert, then catch `DbUpdateException` whose inner `PostgresException` is `UniqueViolation` and treat it as success. The catch must also take the losing row out of the context (`Entry(entity).State = EntityState.Detached`, or `ChangeTracker.Clear()`), otherwise the next `SaveChanges` on the same scoped context retries the insert and throws. Atomic state changes use `ExecuteUpdateAsync` (`LoginTokenRepository.MarkConsumedAsync`).
- **Migrations**: generate with the CLI; never hand-write (the `Designer.cs` and snapshot must match). They run automatically at startup. There is no design-time factory, so `dotnet ef` builds the host through `Program.cs`.
- **Data-only migrations**: still generate the (empty) migration with the CLI, then add `migrationBuilder.Sql(...)` to `Up`; see `ClearSentEmailBodies`. To try one against real rows without touching dev data, create a scratch database on the local Postgres container and pass `--connection` to `dotnet ef database update <previous migration>`, seed, then update to latest. On macOS the `dotnet ef` tool leaves a stray `Recall.Web/bin\Debug/` folder (literal backslash) that `.gitignore` does not match; delete it after running any `dotnet ef` command.
- **Seeding**: none. In local dev the hardcoded dev user row must be inserted by hand (see section 12).

**Query patterns worth knowing before optimizing**

- No classic N+1 over navigation properties; no `Include` calls anywhere.
- `Dashboard`, `Library`, `Favorites` and `NewEpisodeNotificationTimer` load one full `SeriesAggregate` per tracked/liked/watched series with `Task.WhenAll` on every request: a Redis GET plus deserialization of every episode and character. This is fine as it stands; see the note under TheTVDB in section 7. `Profile` and `Stats` (both through `StatsService`) do the same for every watched series and movie, from the caches only and 16 at a time.
- A Stats page load (and a Profile load) sends three queries for one user's rows: every episode watch, every movie watch, and the rating counts (`GROUP BY value`). Each is served by an existing index that leads with `user_id` (bitmap index scan, 0.06–0.32 ms for one user's 1,000 watches, 200 movies and 100 ratings among 60 users'; measured 2026-10-02, and `StatsQueryTests` asserts the plans). `source` is not indexed: it is read with the user's rows and judged in memory.
- The activity query behind the continue-watching order (`GROUP BY series_tvdb_id` with `max(watched_utc)` over one user's `episode_watch` rows) is served by the existing `(user_id, series_tvdb_id)` index: a bitmap index scan and a hash aggregate, 0.35 ms for one user's 1,000 watches in a table of 60,000 (measured 2026-10-02 in the PostgreSQL suite, where `ContinueWatchingQueryTests` asserts the plan). Adding `watched_utc` to the index was considered and not done: it would only save the heap fetches of one user's rows.
- `NewEpisodeNotificationTimer` loops series, then users, with one watched-ids query and one or two notification queries per pair.
- The sitemap is capped at 50,000 URLs (`SitemapModel.MaxUrls`): `SitemapService.GetCachedContentAsync` fills series, then movies, then episodes with the room left, most recently refreshed first.
- `RatingRepository.GetSummaryAsync` gets `COUNT` and `AVG` in one grouped query.
- `WatchlistImportRepository.RecalculateJobProgressAsync` counts items per status with a grouped query.
- The notification bell's unread `COUNT` (every signed-in page) is index-backed on `notification(user_id, …)`; it was checked with `EXPLAIN` and left alone.

## 7. External integrations

### TheTVDB (v4 API)

Plan: the **free tier, which requires attribution**. The attribution is the "Metadata provided by TheTVDB" link (to `https://thetvdb.com`) and logo in the footer of `_Layout.cshtml`, which every page that shows TheTVDB data uses. The text must stay visible at every screen width; it may wrap or shrink but is never hidden.

- **A lighter per-series summary was measured and rejected** (2026-10-01). With the dev user tracking 58 series (10,565 episodes), Dashboard, Library and Profile took 20–25 ms of server time reading full aggregates; a cached projection without overviews and characters saved 3–6 ms per request, which did not justify a second cache entry per series. Everything reads the aggregate. Revisit only if these pages get measurably slow.

- `Services/External/TheTvDb/TheTvDbApiClient.cs` is pure transport: typed `HttpClient` whose 8 s timeout bounds one attempt (body included), bearer token attached per request.
- `TheTvDbClientState` **must stay a singleton**. It holds the cached token and a `SemaphoreSlim(5)` throttle shared by all client instances. On a 401 the client passes the specific stale token back, so only one of several racing requests re-authenticates; the request is resent once inside the same attempt.
- Non-success responses throw `TheTvDbApiException`.
- **Resilience** (`Infrastructure/External/ExternalHttpResilience.cs`, `Microsoft.Extensions.Http.Resilience`). Retries happen on network errors, timeouts, 408, 5xx, and for TheTVDB on a 429 whose `Retry-After` is at most 2 s; waits between attempts are capped at 2 s.
  - **TheTVDB**: 2 retries, run by `TheTvDbApiClient.SendAsync` through a keyed `ResiliencePipeline<HttpResponseMessage>`, *outside* the throttle. Each attempt takes its own slot, so a request backing off does not occupy one of the five slots, and time queueing for a slot counts against no timeout. Worst case 3 × 8 s + 2 × 2 s = 28 s, inside the 30 s budget (`TheTvDbOverallBudget`).
  - **OMDb**: an ordinary resilience handler inside the `HttpClient`: 1 retry, never on 429, 8 s per attempt, 20 s client timeout overall. The retry takes its own permit from `IOmdbRequestBudget` and is skipped when none is left, because every request OMDb receives counts against the daily limit.
  - Changing an attempt timeout or retry count means changing the constants in that file; tests assert the worst case still fits. Do not move the TheTVDB retries into a handler: that puts them back inside the throttle slot.
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
- **Background art**: `SeriesAggregate.BackgroundUrl` is the thumbnail of the best-scored artwork of type 3 (TheTVDB's 16:9 series background, "fanart") from the extended record's `artworks`. It was added on 2026-10-02, so (see the staleness note above) a row cached earlier has none until it is refreshed: within 12 h for a keep-updated series, up to 30 days for the rest.
- `SearchAsync` and `ResolveByRemoteIdAsync` are not cached.
- **Genres**: `SeriesAggregate.Genres` is TheTVDB's genre list, the same vocabulary `MovieAggregate.Genres` has always had. It was added on 2026-10-02, so (see the staleness note above) a series row cached earlier has none until it is refreshed. Series Details shows TheTVDB's genres and falls back to OMDb's (a different vocabulary: "Sci-Fi" for "Science Fiction") only while the series has none; Stats never uses OMDb's.
- `GetCachedSeriesAggregateAsync` and `GetCachedMovieAggregateAsync` are the reads that never reach the API: the same Redis and Postgres tiers, and null when neither has the title. Use them for work that must not spend quota: background jobs (the weekly digest) and pages that must cost nothing per view (Stats, Profile).

### OMDb

Plan: the **free tier, 1,000 requests a day**. `Omdb:MaxRequestsPerDay` defaults to 900 to leave headroom.

- `Services/External/Omdb/OmdbApiClient.cs`: typed client, 20 s overall timeout (8 s per attempt, one retry), API key in the query string, returns `null` for "not found". It has no cache of its own.
- Snapshots live in `cached_series_omdb`, `cached_movie_omdb` and `cached_episode_omdb`, refreshed at most every 30 days. A row with a null payload records "checked, nothing found".
- **Types**: `OmdbSeries`, `OmdbMovie` and `OmdbEpisode` (`Domain/Omdb/`) all derive from `OmdbTitle`, whose `[JsonPropertyName]` values are also the storage format of the three `payload` columns. Rows cached before the types were split still load (unknown keys are ignored). Do not rename a JSON property there; adding one is safe. The client exposes `GetSeriesAsync` / `GetMovieAsync` / `GetEpisodeAsync`.
- Series and movies are enriched proactively by hourly jobs. **Episodes are enriched lazily on the request path**, the first time a *signed-in* user opens `Episodes/Details` (`DetailsModel.LoadOmdbAsync`). An anonymous request only ever reads the cached snapshot.
- Every request that reaches OMDb must be covered by a permit from the singleton `IOmdbRequestBudget` (`FixedWindowRateLimiter`, default 900/day via `Omdb:MaxRequestsPerDay`). Call sites take one before calling the client; the client's retry takes its own. A new call site must do the same.

### Cloudflare Turnstile

`Services/Authentication/TurnstileVerifier.cs` posts to `siteverify` from the login page, with a 10 s timeout and no retry. It is disabled when either key is blank and fails closed on any failure: a rejected token, a network error, an error status, an unreadable body, or a timeout. Only the caller's own cancellation propagates.

### SMTP

`Services/MailService.cs` uses `System.Net.Mail.SmtpClient`. In Development it writes `.eml` files to `Recall.Web/mail-pickup/` instead of sending. The queue is drained by priority, then age: `MailService.NormalPriority` (0, sign-in links) before `DigestPriority` (10), so a sign-in link never waits behind digests. A message with a `ListUnsubscribeUrl` is sent with the two one-click unsubscribe headers.

### Keys

User secrets in development (`UserSecretsId` in the csproj); environment variables from `.env.prod` in production (`TheTvDb__ApiKey`, `TheTvDb__Pin`, `Omdb__ApiKey`, `Mail__*`, `Turnstile__*`).

## 8. Authentication and authorization

- No ASP.NET Core Identity, external providers or JWT. Cookie authentication only (`AddCookieAuthentication`): cookie `Recall.Auth`, 30-day sliding expiry, HttpOnly, SameSite=Lax, Secure in Release builds. Login and access-denied paths are both `/Account/Login`.
- **Cookie revalidation**: `Infrastructure/Authentication/RecallCookieEvents.cs` (`options.EventsType`) re-reads the user row at most every 5 minutes per session. A missing user is signed out; a changed username, email or role is reissued into the cookie. The last-check time is stored in the cookie's own properties. A database error keeps the session and retries on the next request. `RecallPrincipal.Create` is the single definition of the claim set; use it when issuing a cookie.
- **Request a link** (`PasswordlessAuthService.RequestLoginAsync`): normalize email → optional allowlist (`Login:AllowedEmails`; empty means open registration) → `ILoginAbuseGuard` per-address daily cap and site-wide hourly cap → get or create the user → per-user resend cooldown → invalidate earlier tokens → store the SHA-256 hash of a 32-byte random token → queue the email. The page shows the same "link sent" result in every case.
- **Redeem** (`RedeemAsync`, `Pages/Account/Verify`): look up an unconsumed, unexpired hash, then `MarkConsumedAsync` (atomic `UPDATE … WHERE ConsumedUtc IS NULL`). The result of that update is checked so two simultaneous redemptions cannot both succeed. Then `SignInAsync` with `NameIdentifier`, `Name`, `Email` and `Role` claims.
- **Bot defenses on the login form**: honeypot field, minimum 2 s render-to-submit time, Turnstile, per-IP rate limit on requests for a link (`login-email` policy, `LoginEmailPartition`: 8 POSTs per 5 minutes; page loads are never counted, so reloading cannot lock anyone out) and a global limiter on POSTs to `/Account/Login` (300 per minute). Both rate-limit policies live in `AddRateLimiting`.
- **Roles**: `User` and `Admin` (`UserRole` enum; `Roles` constants for attributes). Promotion to Admin is a manual database edit.
- **Per-user scoping**: `ICurrentUserService.UserId` (parsed from the claim) is passed into every repository call, and every user-data query filters on `UserId`. There are no global query filters.
- **`DevAuthMiddleware`**: in Debug builds every non-file request runs as a fixed admin (`11111111-1111-1111-1111-111111111111`, `dev-user`, `dev@example.com`). It skips itself when `ASPNETCORE_ENVIRONMENT=Test` and is compiled out of Release. It does not create the user row.

## 9. Background work

Quartz.NET with the default in-memory store, registered in `AddScheduledJobs(configuration)`. Implementations are in `Infrastructure/Timers/`, all `[DisallowConcurrentExecution]`. The hosted service waits for running jobs on shutdown.

| Job | Interval | Work per run |
|---|---|---|
| `UpdateTvDbInfoTimer` | 60 min | Up to 10 series aggregates: `KeepUpdated = true` and older than 12 h, or any other row older than 30 d; tracked series first, then oldest. Then up to 25 episodes older than 30 d, or titled "TBA" and older than 12 h, or aired without an image (max 5 attempts) |
| `UpdateMovieInfoTimer` | 60 min | Up to 10 movie aggregates, same two tiers; movies on a watchlist, watched or liked first |
| `UpdateOmdbInfoTimer` | 60 min | Up to 30 series whose OMDb snapshot is missing or older than 30 d |
| `UpdateMovieOmdbInfoTimer` | 60 min | Same, for movies |
| `MailTimer` | 1 min | Sends up to `Mail:BatchSize` (20) queued emails; gives up after `MaxSendAttempts` (5) |
| `NewEpisodeNotificationTimer` | 6 h | For up to 500 tracked series, notifies each tracking user about episodes aired in the last 3 days that they have not watched; one notification per series per user |
| `WatchlistImportTimer` | 1 min | Resolves up to 15 pending import rows through TheTVDB's remote-id search. A rated movie is marked watched and rated; an unrated movie goes on the watchlist; a series goes into the library (and is rated if the row has a rating) |
| `PruneOldDataTimer` | 24 h (first run 90 s after start) | Deletes rows past their retention period, one `ExecuteDeleteAsync` per category via `IDataRetentionRepository`: login tokens 7 d after they expired or were consumed; emails 30 d after they were sent or gave up; notifications 90 d after they were read; `notified_episode` rows after 30 d; completed import jobs and their items after 90 d; weekly-digest ledger rows after 60 d. Never touches a queued email, an unread notification or an import still processing |
| `WeeklyDigestTimer` | 60 min (first run 2 min after start) | Does nothing unless `Digest:Enabled` (on in production). When a digest is due, queues it for up to `Digest:MaxPerRun` (default 100; 50 in production) opted-in users not yet dealt with for that week |

`Jobs:Disabled` (a list of job class names, empty by default) keeps a job from being scheduled at all; a name that is not a job fails startup. The UI review instances use it to switch off `WatchlistImportTimer`. A new job must be added to `ScheduledJobNames` (startup fails otherwise).

There are no other hosted services, queues or message brokers. The email and import "queues" are database tables.

## 10. Configuration and secrets

- `appsettings.json`: defaults, including the local dev connection strings (`Host=localhost…Password=devpassword`, `localhost:6379`) and empty API keys.
- `appsettings.Development.json`: log levels only. `appsettings.Production.json`: Redis at `redis:6379`, empty `DefaultConnection`, log file under `/var/log/recallapp`.
- Redis connection: `REDIS_CONNECTION` environment variable, falling back to `ConnectionStrings:RedisConnection`.
- Library grouping: the `Library` section (`DormantAfterDays`, default 90, 0 turns the "haven't watched in a while" group off; `PremiereReturnDays`, default 14). See "Continue watching" in section 5.
- Weekly digest: the `Digest` section (`Enabled`; `DayOfWeek` Friday; `HourUtc` 15; `CatchUpHours` 48; `MaxPerRun`) and `Site:BaseUrl` (no default; required when the digest is enabled, or startup fails). `appsettings.json` has `Enabled` false and `MaxPerRun` 100; production overrides them in `.env.prod` with `Digest__Enabled=true` and `Digest__MaxPerRun=50`. `README.md` describes the production settings and the checks to repeat when the mail provider or sending domain changes.
- Retention periods: the `Retention` section (`LoginTokenDays`, `EmailDays`, `ReadNotificationDays`, `NotifiedEpisodeDays`, `DigestLedgerDays`, `ImportJobDays`), in days; 0 or less disables that category. Keep `NotifiedEpisodeDays` well above the notification job's 3-day look-back, or users are notified twice.
- Production secrets come from `.env.prod`, loaded by `compose.prod.yml`. `.gitignore` excludes `.env*`; `.env` and `.env.prod` exist in the working directory but are not tracked.
- **No real secret is committed.** The only credential in the repository is the local-dev Postgres password `devpassword` (also in `README.md`).

## 11. Testing

- 1,107 tests, all passing as of this writing: 1,005 in `Recall.Tests` and 102 in `Recall.Tests.Postgres`. NUnit + Moq + AwesomeAssertions. Folders in `Recall.Tests` mirror `Recall.Web`.
- **Persistence tests** use a real `AppDbContext` on in-memory SQLite (`SqliteConnection("DataSource=:memory:")` + `EnsureCreatedAsync`), not mocks. See `LoginTokenRepositoryTests.cs` for the pattern.
- **Covered**: `PasswordlessAuthService`, `MailService`, `TheTvDbService`, `TheTvDbApiClient`, `TheTvDbClientState`, snapshot stores, watch progress and watch time, notifications, favorites, sitemap, watchlist import and CSV parser, mappings, health check, OMDb budget, trusted forwarded headers (run through the real `ForwardedHeadersMiddleware`), the retention deletes and `PruneOldDataTimer`, audit timestamps, the OMDb JSON format, and UTC air dates. Tests that need a clock use `TestSupport/FixedTimeProvider`.
- **Page-model tests** (`Recall.Tests/Pages/`) construct the page model directly with Moq dependencies and call the handler. `TestSupport/PageModelTesting.cs` supplies `WithTempData()`, `WithHttpContext()` (for handlers that set a status code or read a header) and `SuccessToast()` / `ErrorToast()` / `InfoToast()`. Covered: Dashboard, Library, and every POST handler on the three Details pages (sign-in guard, validation, each outcome's toast, the error path). `SeriesDetailsGetTests` and `EpisodeDetailsGetTests` cover what the Details pages decide on load: the default season, the header's primary action, and the status code of a failed load. `TitleSummaryTests` and `CastBuilderTests` cover the summary line and the cast and crew lists. `ContinueWatchingOrderTests` (in `Recall.Tests/Services/`) covers the ordering rule, the dormant threshold, the premiere that brings a series back and what counts as a premiere; the Dashboard and Library tests cover that both pages use it (the link count, the Watching count, the dormant view). `SearchModelTests`, `ProfileModelTests`, `ImportWatchlistModelTests` and `NotificationsModelTests` cover what those pages decide: the library badges, the import summary and outcome counts, the posters and "Mark read". `DeleteAccountModelTests` covers the deletion flow (confirmation required, a wrong username rejected, the only-admin guard, sign-out and toast). The weekly digest is covered in `Recall.Tests/Services/Digest/` (`DigestBuilderTests` for the selection, `DigestEmailRendererTests`, `DigestUnsubscribeTokensTests`, `DigestScheduleTests`, `DigestComposerTests` for the cache-only read, `WeeklyDigestServiceTests` for the run and the job, `DigestStartupTests` for the startup check), in `DigestRepositoryTests` (the ledger), `DigestUnsubscribeTests` and `DigestPreviewModelTests` (pages), and by the mail tests for the headers and for a sign-in link overtaking a hundred queued digests. Stats is covered in `Recall.Tests/Services/Stats/` (`StatsBuilderTests` for every metric, the source rule, missing runtimes and titles that are not cached; `StatsServiceTests` for the cache-only reads, with a strict mock that fails on any other TheTVDB call; `StatsFormatTests`) and in `StatsModelTests` (the lead, the bars, the sparse and empty states, the failure path). `WatchProgressServiceTests`, `EpisodeWatchRepositoryTests`, `MovieWatchRepositoryTests` and `MovieTrackingServiceTests` cover which source each way of marking records. `PrivacyModelTests` covers what the privacy text reads from configuration, and `LoginRateLimitTests` (with a pipeline test that reloads the sign-in page thirty times) that only requests for a link are rate limited.
- **Job tests** (`Recall.Tests/Infrastructure/Timers/`) call `Execute` with mocked stores and services and check the per-run cap and that one failing item doesn't stop the batch. All nine jobs are covered.
- **Pipeline tests** (`Recall.Tests/Pipeline/`) start the real app with `RecallWebApplicationFactory`: environment `Test` (so `DevAuthMiddleware` stands aside), SQLite instead of Postgres, an in-memory distributed cache instead of Redis, a mocked `ITheTvDbService`, migrations off, and the Quartz hosted service removed. They need nothing running locally or in CI. `Program.cs` ends with `public partial class Program;` for this. Keep this set small: public page renders, protected pages redirect to login, POSTs without an antiforgery token are rejected.
- **HTTP clients** are tested with `TestSupport/StubHttpMessageHandler`; anything needing a clock uses `TestSupport/FixedTimeProvider`.
- **UI checks** are not part of `dotnet test`. `./ui-review/review.sh all` clones `recall_db` into two throwaway databases (never seed `recall_db` itself: it holds real data), runs three app instances, (with the import job switched off through `Jobs:Disabled`, so waiting import rows are never looked up on TheTVDB), with the weekly digest enabled and its job off, and with pending migrations applied to the clones at startup (never to `recall_db`), and captures every page at 1280 and 390px with Playwright and axe-core into `ui-review/screenshots/` (`report.json` has the axe results, overflow and headings per shot). After the phase 0 foundation work the run has no axe violations; keep it that way. It spends roughly 100 TheTVDB requests per run. The scan does not go inside frames (the digest preview's sandboxed frame cannot run scripts, and a scan of it would never return). The seed spreads five finished or up-to-date series over the past year as `Single` watches, so the Stats chart has something in every month, while the dev user's own history (which the migration labels `Bulk` in the clone) sits under the chart's footnote. A state that cannot sit in the seed because a background job would change it (an import still running) is put in place for its one shot and removed again: the capture calls `review.sh import-progress on|off`, which runs `import-in-progress.sql`. The Stats page's sparse state is done the same way on the empty clone: `review.sh stats-sparse on|off` and `stats-sparse.sql`. What `dotnet test` does cover of the UI: the pipeline tests check the status and error pages, the skip link and `aria-current`; `IconsTests` checks every icon constant; `DisplayDateTests` the date formats.
- **PostgreSQL tests** (`Recall.Tests.Postgres`) cover what SQLite cannot. `PostgresSuite` (a `[SetUpFixture]`) starts one `postgres:18.1` container per run with `Testcontainers.PostgreSql` and builds a template database by applying the real migrations; a fixture derives from `PostgresFixture` and gets its own database cloned from the template (`CREATE DATABASE … TEMPLATE`). Tests in a fixture share that database, so each works on its own user (`SeedUserAsync`) and its own ids (`NextId`); a fixture whose queries take "the first N rows" truncates the tables it reads in `[SetUp]`. What is covered:
  - `Migrations/`: all migrations apply to an empty database, re-applying is a no-op, the model has no changes missing from the migrations (`HasPendingModelChanges`), and the five data-moving migrations (the latest: `WatchSource`, which classifies existing watch rows) are tested by stopping at the migration before (`MigrationDatabase.MigrateToAsync`), seeding rows with plain SQL, applying the rest and asserting. A new migration that contains `migrationBuilder.Sql` gets a test here.
  - `Concurrency/UniqueViolationTests`: every `UniqueViolation` catch block. `CompetingWriteInterceptor` inserts the competing row just before the first `SaveChanges`, so the violation happens on every run. Each test also checks the context still saves afterwards.
  - `Concurrency/TrackedSeriesXminTests`: the `xmin` token (insert through EF, version changes on update, stale update and stale delete throw `DbUpdateConcurrencyException`).
  - `Snapshots/JsonbSnapshotTests`: `jsonb` round-trips for the seven cache tables, that the payload is stored as a document and not a quoted string, and that PostgreSQL rejects a payload that is not JSON.
  - The weekly digest: the ledger's unique index under a competing write (in `UniqueViolationTests`), and in `PostgresQueryTests` the recipients query, the preference updates, and that a long text part fits the `email.body` column.
  - `Queries/StatsQueryTests`: the three queries of a Stats page load return the user's rows with their source, each is one statement, and each plan uses an index that leads with `user_id` with no sequential scan. `Queries/CommandCapture` (shared with the test below) records the SQL EF sent so the same statement can be explained.
  - `Queries/ContinueWatchingQueryTests`: the latest watch per series for one user (other users' watches ignored), sent as one query, and that its plan uses the `(user_id, series_tvdb_id)` index with no sequential scan.
  - `Queries/PostgresQueryTests`: the OMDb anti-join, the refresh queues that order by a correlated `EXISTS`, the import queue and its `GROUP BY` progress count, the rating average, and case-insensitive user names.
  - `Account/AccountDeletionTests`: a user with rows in every user-owned table is deleted and nothing of theirs remains while another user's data is untouched; the Recall rating changes; the only admin is refused; signing in again gives a new, empty account.
  - `WatchTracking/BulkWatchUndoTests`: the bulk-watch timestamp is stored exactly as returned and the undo removes exactly that batch.
- **Docker and the PostgreSQL tests**: without a reachable Docker the suite is reported as ignored (every test in it skipped, with the reason), so `dotnet test Recall.sln` stays green; with the `CI` environment variable set it fails instead. Only "Docker unreachable" is turned into ignored; a failed image pull or a broken migration fails the run. `DockerAvailabilityTests` sits outside the suite's namespace so it runs either way. Measured on 2026-10-01 on the development Mac with the image cached: 7.2 s for the project on its own (container 2.5 s, migrations 0.6 s, tests about 3 s); `dotnet test Recall.sln` stays at about 8.3 s because the two test projects run in parallel.
- **Not covered**: the GET side of the Movie Details, Favorites and Admin page models; the Import upload handler; `DevAuthMiddleware`; most read methods of the `Like` and `Notification` repositories. `TrackedSeriesEntity` cannot be inserted through EF on SQLite at all (`xmin` becomes an ordinary NOT NULL column); tests in `Recall.Tests` that need a tracked series seed it with raw SQL, see `TvdbSnapshotStoreTests`. Anything that depends on `xmin`, `jsonb` or a unique violation belongs in `Recall.Tests.Postgres`.

```bash
dotnet test Recall.sln                                              # everything (the PostgreSQL tests need Docker)
dotnet test Recall.Tests                                            # without the PostgreSQL tests
dotnet test Recall.Tests.Postgres                                   # only the PostgreSQL tests
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

**CI/CD** (`.github/workflows/dotnet.yml`): on push and pull request to `main`, restore, Release build, test `Recall.Tests` with coverage (summary only; coverage does not gate), then test `Recall.Tests.Postgres` in its own step (the runner's Docker starts the container; no coverage is collected there, so the summary reflects `Recall.Tests` only). On push, build `Dockerfile.prod`, push `ghcr.io/<user>/recall:latest` and `:<run number>`, then SSH to the server, run `dump_db.sh` and `docker compose -f compose.prod.yml up -d --pull always`. There is no lint or format step. `nightly-build.yml` builds and tests the whole solution daily at 05:00 UTC, PostgreSQL tests included.

**Hosting**: a single Docker host, single app instance (a permanent assumption). `compose.prod.yml` runs the app (published port 8701, logs and Data Protection keys on bind mounts), `postgres:18.1` (host port 5433) and `redis:7`.

**Reverse proxy**: the app container sits behind nginx installed directly on the host (apt, systemd), which proxies to the published port 8701. nginx already sends `Host`, `X-Forwarded-For` and `X-Forwarded-Proto`; the app builds sign-in links from the scheme and host and keys its rate limiters on the client IP. The app's `TrustedProxies` default (loopback plus the private ranges) matches this setup, so it needs no server configuration. This setup works and must not change (see the working agreement).

**Backups**: taken on the server with `pg_dump` through `docker exec` into the `recall_postgres` container, gzipped into `/var/backups/recall/`. `README.md` has the backup and restore commands; they read the user, database and password from `.env.prod` rather than spelling them out.

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
- **Episode ordering**: `EpisodeOrderingExtensions` has the two orders and nothing else should sort episodes: `OrderBySeasonAndEpisode` (TheTVDB's listing order, specials first; notifications, raw episode loads) and `OrderByWatchOrder` (specials last; everything about watching, through `WatchProgressCalculator.Order`).
- **Recording a watch**: call `IWatchProgressService`; it validates the series/episode pair and the air date, and records the right `WatchSource`. Page handlers map the returned `EpisodeWatchOutcome` to a toast.
- **Anything laid out over time** (a chart, a "this month" figure, a streak if one is ever built) counts only watches whose source `IsDated`; totals count every row. See "Watch source" in section 5.
- **Charts** are server-rendered lists with their figures in text; see "Stats" under "Design system". No chart library, no script.
- **Movie watchlist and watched state**: call `IMovieTrackingService`; it keeps "on the watchlist" and "watched" mutually exclusive, and takes the `WatchSource` of a new watch.
- **Parallelism**: only fan out over code that uses `IDbContextFactory`. Never run two operations on the scoped `AppDbContext` at once.
- **Logging**: structured message templates with `ILogger<T>`; no string interpolation in log calls.
- **Time**: inject `TimeProvider` (registered as `TimeProvider.System`) wherever the current date decides behavior, and get the date with `AirDate.Today(timeProvider)` — the UTC date, the same on a developer machine and in production. `AirDate.IsInFuture(aired, today)` is the one "has it aired" rule; an unknown air date is not treated as unaired. Do not call `DateTime.Today`, `DateTime.Now` or a static clock. Views read `Model.Today`. Persistence timestamps still use `DateTime.UtcNow` directly.
- **Retention**: a new table that only ever grows needs a delete in `IDataRetentionRepository`, a period in `RetentionOptions`, and a line in `PruneOldDataTimer`. Each category is its own `ExecuteDeleteAsync` in its own try/catch.
- **Audit columns**: an entity with `CreatedUtc` and `UpdatedUtc` implements `IHasAuditTimestamps`; never set either by hand.
- **Handlers on public pages** start with `currentUserService.TryGetUserId(out var userId)`; do not re-derive the check from `IsAuthenticated`.
- **Comments**: the codebase explains *why* in comments and XML docs on non-obvious code; keep that density.
- **SEO**: pages are `noindex` unless they set `ViewData["Robots"]`.
- **Design system**: tokens, type scale, icon constants, the page frame, `_EmptyState` and `DisplayDate` are described under "Design system" in section 4. In short: no literal colours, font sizes or radii outside `tokens.css`; no `!important`; icons through `Icons.*`; dates through `DisplayDate`; one `h1` per page; mono for data only.
- **Culture**: the process culture is pinned to `en-US` (`Infrastructure/Hosting/AppCulture.cs`). Do not set `LANG`/`LC_ALL` in images or add request localization; machine-readable output (sitemap dates, JSON-LD, anything parsed back) should still pass `CultureInfo.InvariantCulture` explicitly.
- **Public pages and quota**: a page reachable without sign-in must not call OMDb, and if it can trigger a TheTVDB fetch it needs the `public-details` rate-limit policy. `PublicDetailsRateLimitTests` checks the three current pages carry it.

## Observations

Ranked by impact. Items marked fixed are kept for the record, with whatever remains of them.

1. **Anonymous pages spending upstream quota: fixed for OMDb, bounded for TheTVDB.** Anonymous requests never call OMDb, the Details pages are rate-limited per anonymous IP, and the sitemap is capped. What remains by design: an anonymous visitor can still make the app fetch an uncached title from TheTVDB (up to 60 pages a minute per IP; one uncached long-running series is itself hundreds of calls), and each such fetch adds rows to the cache tables and, later, URLs to the sitemap. A crawler spread across many IPs is not bounded by the per-IP limit.
2. **Forwarded headers trusted from any source: fixed in the app.** `X-Forwarded-*` is applied only when the request comes from loopback or a private range, which is how nginx on the host reaches the container. A caller arriving from a public address cannot forge its client IP.
3. **Sessions that could not be revoked: fixed, within 5 minutes.** Deleting or demoting a user takes effect at the next revalidation. A user can delete their own account (section 5). There is still no "sign out everywhere" for a user who keeps their account, and no admin UI to do either; both remain manual database edits.
4. **No HTTP resilience: fixed.** Both metadata clients retry transient failures; see section 7. Not added: a circuit breaker, so a long TheTVDB outage still costs every request its retries.
5. **Cached aggregates never refreshing: fixed.** Rows without `KeepUpdated = true` are now refreshed every 30 days. Remaining: `cached_series_extended` still has no refresh path, but nothing reads it any more (`ITheTvDbService.GetSeriesByIdExtendedAsync` has no caller outside tests), so the table and method are candidates for removal. The refresh cap (10 series and 10 movies an hour) bounds how fast a large, crawler-filled cache cycles.
6. **Two sources for a series' episode list: fixed.** Display, progress, "mark watched through" and the prior-unwatched prompt all read the aggregate.
7. **Write handlers trusting client-supplied id pairs: fixed for watches, likes and ratings on episodes.** They go through `IWatchProgressService` or refuse when the parent series can't be resolved. Still unvalidated: series and movie like/rating handlers accept any positive id without checking it exists on TheTVDB.
8. **Single-instance assumptions.** Login abuse limits, the OMDb daily budget, the TheTVDB token and the Quartz schedule are all in process memory, and migrations run at startup. Every deploy resets the OMDb budget and login counters. A second instance would double every job.
9. **Test gaps: mostly fixed.** Page models, all jobs, `LoginAbuseGuard`, `TurnstileVerifier`, `OmdbApiClient` and a small pipeline suite are tested, and `Recall.Tests.Postgres` runs the migrations, the `UniqueViolation` catch blocks, the `xmin` token, `jsonb` and the provider-specific queries against real PostgreSQL (see section 11 for what remains). Writing those tests found that `EpisodeWatchRepository.MarkWatchedAsync`, `LikeRepository.ToggleAsync` and `MovieWatchRepository.ToggleAsync` left the row that lost the race pending in the context; they now detach it. Still open: CI coverage is informational only and does not include the PostgreSQL tests.
10. **Dead or unused code and dependencies: fixed.** The four unused packages, the controller and session registrations, the unused Redis multiplexer, `AppDbContextFactory`, `UserItem`/`UserMappings`, `site.js`, the empty import map and the stale csproj items are gone. Left alone: `Microsoft.AspNetCore.Mvc.Testing` in the test project (unused, see 9), and `Recall.Web/Dockerfile` and `compose.yaml` (Docker setup is not changed without being asked).
11. **Convention drift: mostly fixed.** "Already in your library" is a return value, the admin counts come from `IAppUserRepository`, and movies and episodes have their own OMDb types. Remaining: `IAppUserRepository` still returns entities, and `EpisodeOmdbSnapshotStore` uses the scoped context while its two siblings use the factory.
12. **State-changing GETs: fixed.** Logout and notification-open are POST-only. Convention: anything that changes state is a POST handler behind the antiforgery token.
13. **Unbounded tables: fixed.** `PruneOldDataTimer` deletes settled login tokens, finished emails, read notifications, the notified-episode ledger and completed imports on the periods in `Retention`. By design it keeps unread notifications and anything still in progress, so an account that never reads its notifications still accumulates them.
14. **Duplication: partly fixed.** `ApplyAuditTimestamps` is one loop over `IHasAuditTimestamps`, and the Details POST handlers share `TryGetUserId`. Deliberately left: the three OMDb snapshot stores and the two OMDb jobs are still near copies.
15. **Local time for air dates: fixed.** Every air-date check, and the Dashboard header, uses the UTC date from an injected `TimeProvider`. There is still no notion of the user's time zone.
16. **Deployment details.** The Swedish locale is gone: `Dockerfile.prod` no longer generates or sets `sv_SE.UTF-8`, and the app pins its own culture (`AppCulture.PinToEnglish`, `en-US`) at startup, so dates and numbers render in English whatever the host is set to. This was confirmed, not inferred: on a Swedish-locale machine the Dashboard header read "torsdag, oktober 1" before and "Thursday, October 1" after. Still open: the log directory is `chmod 777`; the image installs fonts nothing in the app uses; the Copilot setup workflow uses `postgres:16-alpine` against 18.1 elsewhere.
17. **Stale documentation and comments: fixed.** `AGENTS.md` is a pointer to this file, the README backup commands no longer `cd` into a Receptus folder, and the comments about where jobs are scheduled, where cookie auth is wired, and which pages are public are corrected. The README's backup and restore commands now match production (see section 12).
18. **Debug builds log the raw login token** (`PasswordlessAuthService`, inside `#if DEBUG`). Deliberate for local sign-in, but a Debug build must never be deployed.

## Open questions

Answered (2026-10-01), recorded here because the code alone does not show them:

- **Proxy**: nginx on the host in front of the container; see section 12.
- **Registration** is open to anyone in production (`Login:AllowedEmails` empty), so Turnstile and the in-memory abuse caps carry the load.
- **Public Details pages and the sitemap are meant to drive search traffic.** Keep them indexable; protect upstream quota some other way than requiring sign-in.
- **Movies are first-class**: they have a watchlist ("want to watch"), built as `tracked_movie`.
- **"Aired" is judged in UTC.** **A single app instance** is a permanent assumption.
- **API plans**: OMDb free tier (1,000 requests a day); TheTVDB free tier, which requires attribution.
- **Backups** run on the server: `docker exec` into the `recall_postgres` container (database `recall_db`, user `recall_user`), `pg_dump`, gzipped into `/var/backups/recall/`. The backup script itself is deliberately not in the repository; `README.md` has the commands.

Still open:

1. What are TheTVDB's rate limits on the free tier? (The retry counts and the 60-per-minute anonymous page limit are guesses until then.)
2. Are the retention periods in `Retention` the ones you want? (Account deletion exists now, and the Privacy page describes it and the retention periods.)
3. Are the backups in `/var/backups/recall/` copied off the server, and has a restore been tested?
4. Should `Recall.Web/Dockerfile` and `compose.yaml` (IDE-generated, unused by CI) be kept?
5. The project memory notes that Recall mirrors account features from the sibling Receptus repository. Which Receptus features are still to be ported?

## Quick facts

- **Stack**: ASP.NET Core 10 Razor Pages, C# 14, one web project plus two test projects. Tracks series (library + per-episode watches) and movies (watchlist + watched).
- **Data**: PostgreSQL 18 through EF Core 10 (Npgsql); Redis 7 as a read-through cache; metadata stored as JSON snapshots, not relational tables.
- **External APIs**: TheTVDB v4 (primary metadata), OMDb (IMDb ratings), Cloudflare Turnstile, SMTP.
- **Auth**: passwordless magic link → 30-day cookie; roles `User`/`Admin`; Debug builds auto-sign-in as a fixed admin.
- **Jobs**: nine Quartz.NET jobs, in-memory schedule, single instance.
- **Front end**: Bootstrap 5.3.3 themed through CSS variables, Phosphor Icons, self-hosted fonts, jQuery for validation; all vendored through `libman.json`, no build step.
- **Run**: `dotnet watch run --project Recall.Web --launch-profile Recall.Web` (needs local Redis and Postgres) → https://localhost:7123
- **Build**: `dotnet build Recall.sln --configuration Release`
- **Test**: `dotnet test Recall.sln` (1,107 tests, NUnit; SQLite in-memory for persistence, plus a Testcontainers PostgreSQL suite that needs Docker and is skipped without it)
- **Deploy**: push to `main` → GitHub Actions → GHCR image → `docker compose -f compose.prod.yml up -d` over SSH.
