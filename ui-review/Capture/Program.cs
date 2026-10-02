using System.Text.Json;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

// Captures every page of Recall at a desktop and a phone width, and records
// what axe-core and a few layout checks say about each one.
//
//   dotnet run --project ui-review/Capture -- \
//     --signed-in http://localhost:7461 --empty http://localhost:7462 \
//     --anonymous http://localhost:7463 --out ui-review/screenshots
//
// Normally started by ../review.sh capture, which also explains the three
// instances. Optional: --only <text> captures just the shots whose name
// contains the text; --install downloads the Chromium build Playwright needs.

var options = Options.Parse(args);

if (options.Install)
    return Microsoft.Playwright.Program.Main(["install", "chromium"]);

Directory.CreateDirectory(options.OutputDirectory);

// Series and movies the seed (../seed.sql) puts in known states.
const int Watching = 81189;      // Breaking Bad
const int UpToDate = 403245;     // Silo
const int AllWatched = 334824;   // Dark
const int NotStarted = 370112;   // Mare of Easttown
const int WatchedMovie = 287533; // Oppenheimer
const int WatchlistMovie = 1305; // Heat

string? cachedEpisodePath = null;

List<Shot> shots =
[
    // ---- Signed in, with a full library --------------------------------------
    new("dashboard", "populated", Site.SignedIn, "/Dashboard"),
    new("search", "empty", Site.SignedIn, "/Search"),
    new("search", "results", Site.SignedIn, "/Search?Query=dark"),
    new("search", "show-more", Site.SignedIn, "/Search?Query=dark",
        Prepare: async page =>
        {
            // The rest of the results are on the page, hidden; each press reveals the next twenty.
            await page.Locator("#showMoreResults").ClickAsync();
            await page.Locator("#searchResults > li:not([hidden])").Nth(39).WaitForAsync();
        }),
    new("search", "no-results", Site.SignedIn, "/Search?Query=zzqqxxzzqq"),
    new("library", "populated", Site.SignedIn, "/Library"),
    new("library", "section-watched", Site.SignedIn, "/Library?section=watched"),
    new("library", "section-dormant", Site.SignedIn, "/Library?section=dormant"),
    new("library", "dormant-expanded", Site.SignedIn, "/Library",
        Prepare: async page =>
        {
            // From 576px the "haven't watched in a while" group is collapsed behind a button; on a phone it is a row.
            var toggle = page.Locator(".tvdb-subgroup__toggle");
            if (await toggle.IsVisibleAsync())
            {
                await toggle.ClickAsync();
                await page.Locator("#dormantSeries.show").WaitForAsync();
            }

            await page.Locator("#dormant").ScrollIntoViewIfNeededAsync();
        }),
    new("series-details", "watching", Site.SignedIn, $"/Series/Details/{Watching}"),
    new("series-details", "up-to-date", Site.SignedIn, $"/Series/Details/{UpToDate}"),
    new("series-details", "all-watched", Site.SignedIn, $"/Series/Details/{AllWatched}"),
    new("series-details", "prior-episodes-modal", Site.SignedIn, $"/Series/Details/{NotStarted}", ViewportOnly: true,
        Prepare: async page =>
        {
            // Marking a later episode while earlier ones are unwatched asks what to do.
            await page.Locator("button.tvdb-watch-dot[data-episode-id]").Nth(2).ClickAsync();
            await page.Locator(".modal.show").WaitForAsync();
        }),
    new("series-details", "toast-success-undo", Site.SignedIn, $"/Series/Details/{NotStarted}", ViewportOnly: true,
        Prepare: async page =>
        {
            await MarkSeasonWatchedAsync(page);
            await page.Locator(".tvdb-toast-stack .alert-success").WaitForAsync();
        },
        // Undo, so the series is back to "nothing watched" for the next shot.
        Cleanup: async page =>
        {
            await page.Locator(".tvdb-toast-stack").GetByRole(AriaRole.Button, new() { Name = "Undo" }).ClickAsync();
            await page.Locator(".tvdb-toast-stack .alert-info").WaitForAsync();
        }),
    new("series-details", "toast-info", Site.SignedIn, $"/Series/Details/{NotStarted}", ViewportOnly: true,
        Prepare: async page =>
        {
            // Mark and undo, which also leaves the series as it was.
            await MarkSeasonWatchedAsync(page);
            await page.Locator(".tvdb-toast-stack").GetByRole(AriaRole.Button, new() { Name = "Undo" }).ClickAsync();
            await page.Locator(".tvdb-toast-stack .alert-info").WaitForAsync();
        }),
    new("series-details", "season-menu", Site.SignedIn, $"/Series/Details/{Watching}", ViewportOnly: true,
        Prepare: async page =>
        {
            await page.Locator("#seasonMenuButton").ScrollIntoViewIfNeededAsync();
            await page.Locator("#seasonMenuButton").ClickAsync();
            await page.Locator(".dropdown-menu.show").WaitForAsync();
        }),
    new("series-details", "remove-confirm", Site.SignedIn, $"/Series/Details/{Watching}", ViewportOnly: true,
        Prepare: async page =>
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Remove from library" }).ClickAsync();
            await page.Locator("#removeFromLibraryModal.show").WaitForAsync();
        }),
    new("episode-details", "watched", Site.SignedIn, $"/Series/Details/{AllWatched}",
        Prepare: async page =>
        {
            await OpenFirstEpisodeAsync(page);
            // Remembered for the signed-out shot: that instance has no TheTVDB
            // key, so it can only show an episode this visit has cached.
            cachedEpisodePath = new Uri(page.Url).PathAndQuery;
        }),
    new("episode-details", "unwatched", Site.SignedIn, $"/Series/Details/{NotStarted}", Prepare: OpenFirstEpisodeAsync),
    new("movie-details", "watched", Site.SignedIn, $"/Movies/Details/{WatchedMovie}"),
    new("movie-details", "watchlist", Site.SignedIn, $"/Movies/Details/{WatchlistMovie}"),
    new("profile", "populated", Site.SignedIn, "/Account/Profile"),
    new("edit-profile", "default", Site.SignedIn, "/Account/EditProfile"),
    new("edit-profile", "validation", Site.SignedIn, "/Account/EditProfile",
        Prepare: async page =>
        {
            await page.Locator("#Input_Username").FillAsync("a");
            await page.Locator("#editProfileSubmit").ClickAsync(new() { Force = true });
            await page.Locator(".field-validation-error, #usernameStatus:not([hidden])").First
                .WaitForAsync(new() { Timeout = 5000 });
        }),
    new("edit-profile", "username-taken", Site.SignedIn, "/Account/EditProfile",
        Prepare: async page =>
        {
            // "saga" is one of the two extra users in the seed.
            await page.Locator("#Input_Username").FillAsync("saga");
            await page.Locator("#usernameStatus:not([hidden])").WaitForAsync(new() { Timeout = 5000 });
        }),
    // In the seeded clone the dev user is the only administrator: the page explains instead of offering the form.
    new("delete-account", "only-admin", Site.SignedIn, "/Account/Delete"),
    // A year of dated watches from the seed, with the dev user's own bulk-marked history under the footnote.
    new("stats", "populated", Site.SignedIn, "/Account/Stats"),
    new("stats", "table-open", Site.SignedIn, "/Account/Stats", ViewportOnly: true,
        Prepare: async page =>
        {
            // The same figures as a table, behind the native disclosure.
            await page.Locator(".tvdb-disclosure > summary").ClickAsync();
            await page.Locator(".tvdb-stats-table").ScrollIntoViewIfNeededAsync();
        }),
    new("favorites", "populated", Site.SignedIn, "/Account/Favorites"),
    new("notifications", "populated", Site.SignedIn, "/Account/Notifications"),
    new("import-watchlist", "completed", Site.SignedIn, "/Account/ImportWatchlist"),
    new("import-watchlist", "in-progress", Site.SignedIn, "/Account/ImportWatchlist",
        Prepare: async page =>
        {
            // An import with rows still waiting only exists for this shot:
            // left in place, the import job would work through it.
            await RunReviewScriptAsync(options, "import-progress", "on");
            await page.ReloadAsync();
            await page.Locator(".tvdb-import-progress").WaitForAsync();
        },
        Cleanup: _ => RunReviewScriptAsync(options, "import-progress", "off")),
    new("admin", "default", Site.SignedIn, "/Admin"),
    new("admin", "digest-preview", Site.SignedIn, "/Admin/DigestPreview"),
    new("digest-unsubscribe", "confirm", Site.SignedIn, "/Admin/DigestPreview", ViewportOnly: true,
        Prepare: async page =>
        {
            // The link needs a signed token; the preview page shows the one for the dev user.
            // Only the page with the button is opened: nothing is unsubscribed.
            var href = await page.Locator("#previewUnsubscribeLink").GetAttributeAsync("href");
            await page.GotoAsync(href!);
            await page.Locator("#unsubscribeConfirm").WaitForAsync();
        }),
    new("privacy", "signed-in", Site.SignedIn, "/Privacy"),
    new("error", "signed-in", Site.SignedIn, "/Error"),
    new("nav", "bell-unread", Site.SignedIn, "/Dashboard", ViewportOnly: true, Prepare: OpenMobileMenuAsync),
    new("nav", "account-menu-open", Site.SignedIn, "/Dashboard", ViewportOnly: true,
        Prepare: async page =>
        {
            // On a phone the account links are rows of the collapsed menu; the
            // avatar dropdown only exists on wider screens.
            await OpenMobileMenuAsync(page);
            var avatar = page.Locator("#accountMenu");
            if (await avatar.IsVisibleAsync())
            {
                await avatar.ClickAsync();
                await page.Locator(".dropdown-menu.show").WaitForAsync();
            }
        }),
    new("dashboard", "catch-up-undo", Site.SignedIn, "/Dashboard", ViewportOnly: true,
        Prepare: async page =>
        {
            // One tap on a catch-up card marks the episode watched; the toast offers the undo.
            await page.Locator(".tvdb-catchup-check button").First.ClickAsync();
            await page.Locator(".tvdb-toast-action").WaitForAsync();
        },
        Cleanup: async page =>
        {
            await page.Locator(".tvdb-toast-action").ClickAsync();
            await page.Locator(".tvdb-toast-stack .alert-info").WaitForAsync();
        }),

    // ---- Signed in, nothing tracked: the empty states ------------------------
    new("dashboard", "empty", Site.Empty, "/Dashboard"),
    new("library", "empty", Site.Empty, "/Library"),
    // The empty clone has a second administrator, so the confirmation form is offered. Nothing is submitted.
    new("delete-account", "confirmation", Site.Empty, "/Account/Delete"),
    new("delete-account", "ready", Site.Empty, "/Account/Delete", ViewportOnly: true,
        Prepare: async page =>
        {
            // The email address is accepted as well as the username, and the dev user's is known.
            await page.Locator("#Confirmation").FillAsync("dev@example.com");
            await page.Locator("#deleteAccountSubmit:not([disabled])").WaitForAsync();
        }),
    new("stats", "empty", Site.Empty, "/Account/Stats"),
    new("stats", "sparse", Site.Empty, "/Account/Stats",
        Prepare: async page =>
        {
            // Only bulk-marked and imported history: totals and top lists, and
            // an explanation where the chart would be. It exists for this shot
            // only, so the other shots of this instance still find nothing.
            await RunReviewScriptAsync(options, "stats-sparse", "on");
            await page.ReloadAsync();
            await page.Locator("#statsTotals").WaitForAsync();
        },
        Cleanup: _ => RunReviewScriptAsync(options, "stats-sparse", "off")),
    new("favorites", "empty", Site.Empty, "/Account/Favorites"),
    new("notifications", "empty", Site.Empty, "/Account/Notifications"),
    new("profile", "empty", Site.Empty, "/Account/Profile"),
    new("import-watchlist", "empty", Site.Empty, "/Account/ImportWatchlist"),

    // ---- Signed out ----------------------------------------------------------
    new("landing", "signed-out", Site.Anonymous, "/"),
    new("login", "default", Site.Anonymous, "/Account/Login"),
    new("login", "validation", Site.Anonymous, "/Account/Login",
        Prepare: async page =>
        {
            await page.Locator("#Email").FillAsync("not-an-email");
            await page.GetByRole(AriaRole.Button, new() { Name = "Send sign-in link" }).ClickAsync();
            await page.Locator("#Email-error, .field-validation-error").First.WaitForAsync(new() { Timeout = 5000 });
        }),
    new("login", "link-sent", Site.Anonymous, "/Account/Login",
        Prepare: async page =>
        {
            await page.Locator("#Email").FillAsync("ui-review@example.com");
            await page.WaitForTimeoutAsync(2500); // the form refuses a submit faster than a person could type
            await page.GetByRole(AriaRole.Button, new() { Name = "Send sign-in link" }).ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }),
    new("verify", "invalid-link", Site.Anonymous, "/Account/Verify?token=not-a-real-token"),
    new("series-details", "signed-out", Site.Anonymous, $"/Series/Details/{Watching}"),
    new("episode-details", "signed-out", Site.Anonymous, $"/Series/Details/{AllWatched}",
        Prepare: async page =>
        {
            if (cachedEpisodePath is null)
                await OpenFirstEpisodeAsync(page);
            else
                await page.GotoAsync(options.BaseUrl(Site.Anonymous) + cachedEpisodePath);
            await SettleAsync(page);
        }),
    new("episode-details", "load-error", Site.Anonymous, "/Episodes/Details/999999999", ViewportOnly: true),
    new("movie-details", "signed-out", Site.Anonymous, $"/Movies/Details/{WatchedMovie}"),
    new("digest-unsubscribe", "invalid-link", Site.Anonymous, "/Digest/Unsubscribe?token=not-a-real-token", ViewportOnly: true),
    new("privacy", "signed-out", Site.Anonymous, "/Privacy"),
    new("error", "signed-out", Site.Anonymous, "/Error"),
    new("not-found", "signed-out", Site.Anonymous, "/this-page-does-not-exist"),
];

Viewport[] viewports =
[
    new(1280, 800, DeviceScaleFactor: 1, IsMobile: false),
    new(390, 844, DeviceScaleFactor: 2, IsMobile: true),
];

if (options.Only is { } only)
    shots = shots.Where(s => s.Name.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync();

var results = new List<ShotResult>();

foreach (var viewport in viewports)
{
    foreach (var shot in shots)
    {
        var result = await CaptureAsync(shot, viewport);
        results.Add(result);
        Console.WriteLine(
            $"{result.File,-58} {(result.Error is null ? "ok" : "FAILED: " + result.Error)}" +
            (result.OverflowPx > 0 ? $"  overflow {result.OverflowPx}px" : "") +
            (result.Violations.Count > 0 ? $"  axe {result.Violations.Count}" : ""));
    }
}

var reportPath = Path.Combine(options.OutputDirectory, "report.json");

// A partial run (--only) replaces its own entries and keeps the rest.
if (options.Only is not null && File.Exists(reportPath))
{
    var previous = JsonSerializer.Deserialize<List<ShotResult>>(await File.ReadAllTextAsync(reportPath)) ?? [];
    var rerun = results.Select(r => r.File).ToHashSet();
    results = previous.Where(r => !rerun.Contains(r.File)).Concat(results).ToList();
}

await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine();
Console.WriteLine($"{results.Count(r => r.Error is null)} screenshots recorded in {options.OutputDirectory}; details in {reportPath}");
return results.Any(r => r.Error is not null) ? 1 : 0;

async Task<ShotResult> CaptureAsync(Shot shot, Viewport viewport)
{
    var file = $"{shot.Name}_{viewport.Width}.png";
    var result = new ShotResult { File = file, Page = shot.Page, State = shot.State, Width = viewport.Width };

    await using var context = await browser.NewContextAsync(new()
    {
        ViewportSize = new() { Width = viewport.Width, Height = viewport.Height },
        DeviceScaleFactor = viewport.DeviceScaleFactor,
        IsMobile = viewport.IsMobile,
        HasTouch = viewport.IsMobile,
        Locale = "en-US",
    });

    var page = await context.NewPageAsync();
    page.Console += (_, message) =>
    {
        if (message.Type == "error") result.ConsoleErrors.Add(message.Text);
    };
    page.Response += (_, response) =>
    {
        if (response.Status >= 400) result.FailedRequests.Add($"{response.Status} {response.Url}");
    };
    page.Dialog += async (_, dialog) => await dialog.AcceptAsync();

    try
    {
        var response = await page.GotoAsync(options.BaseUrl(shot.Site) + shot.Path, new() { WaitUntil = WaitUntilState.Load });
        result.Status = response?.Status ?? 0;
        await SettleAsync(page);

        if (shot.Prepare is not null)
        {
            await shot.Prepare(page);
            result.Url = new Uri(page.Url).PathAndQuery;
        }
        else
        {
            result.Url = shot.Path;
        }

        if (!shot.ViewportOnly)
            await LoadLazyImagesAsync(page);

        await page.ScreenshotAsync(new()
        {
            Path = Path.Combine(options.OutputDirectory, file),
            FullPage = !shot.ViewportOnly,
            Animations = ScreenshotAnimations.Disabled,
        });

        await MeasureAsync(page, result);

        if (shot.Cleanup is not null)
            await shot.Cleanup(page);
    }
    catch (Exception ex)
    {
        result.Error = ex.Message.Split('\n')[0];
    }

    return result;
}

static async Task SettleAsync(IPage page)
{
    try
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 });
    }
    catch (TimeoutException)
    {
        // A slow poster must not fail the shot.
    }

    await page.EvaluateAsync("() => document.fonts.ready");
}

// Images are loading="lazy": scroll through the page so a full-page
// screenshot does not show empty frames below the fold.
static async Task LoadLazyImagesAsync(IPage page)
{
    await page.EvaluateAsync(
        """
        async () => {
            const step = window.innerHeight;
            for (let y = 0; y < document.documentElement.scrollHeight; y += step) {
                window.scrollTo(0, y);
                await new Promise(r => setTimeout(r, 80));
            }
            window.scrollTo(0, 0);
        }
        """);

    try
    {
        await page.WaitForFunctionAsync(
            "() => Array.from(document.images).every(i => i.complete)", null, new() { Timeout = 15000 });
    }
    catch (TimeoutException)
    {
    }
}

static async Task MeasureAsync(IPage page, ShotResult result)
{
    var metrics = await page.EvaluateAsync<JsonElement>(
        """
        () => {
            const doc = document.documentElement;
            const describe = el => el.tagName.toLowerCase()
                + (el.id ? '#' + el.id : '')
                + (typeof el.className === 'string' && el.className.trim() ? '.' + el.className.trim().split(/\s+/).join('.') : '');
            const tooWide = Array.from(document.body.querySelectorAll('*'))
                .filter(el => el.getBoundingClientRect().right > doc.clientWidth + 1 && el.getBoundingClientRect().width > 0)
                .slice(0, 8).map(describe);
            const broken = Array.from(document.images)
                .filter(i => i.complete && i.naturalWidth === 0).map(i => i.currentSrc || i.src);
            return {
                overflow: Math.max(0, doc.scrollWidth - doc.clientWidth),
                tooWide,
                height: doc.scrollHeight,
                imagesWithoutAlt: Array.from(document.images).filter(i => !i.hasAttribute('alt')).length,
                brokenImages: broken,
                title: document.title,
                h1: Array.from(document.querySelectorAll('h1')).map(h => h.textContent.trim()),
            };
        }
        """);

    result.OverflowPx = metrics.GetProperty("overflow").GetInt32();
    result.TooWide = metrics.GetProperty("tooWide").EnumerateArray().Select(e => e.GetString()!).ToList();
    result.PageHeight = metrics.GetProperty("height").GetInt32();
    result.ImagesWithoutAlt = metrics.GetProperty("imagesWithoutAlt").GetInt32();
    result.BrokenImages = metrics.GetProperty("brokenImages").EnumerateArray().Select(e => e.GetString()!).ToList();
    result.Title = metrics.GetProperty("title").GetString();
    result.H1 = metrics.GetProperty("h1").EnumerateArray().Select(e => e.GetString()!).ToList();

    // Not inside frames: the only one in the app is the digest preview's sandboxed
    // frame, where scripts are off, so the scan could never be injected and would wait forever.
    var axe = await page.RunAxe(new Deque.AxeCore.Commons.AxeRunOptions { Iframes = false });
    result.Violations = axe.Violations
        .Select(v => new Violation(
            v.Id,
            v.Impact,
            v.Help,
            v.Nodes.Length,
            v.Nodes.Take(5).Select(n => n.Html.Length > 200 ? n.Html[..200] + "…" : n.Html).ToList(),
            v.Nodes.Take(3).Select(n => n.Any.Concat(n.All).Concat(n.None).FirstOrDefault()?.Message ?? "").ToList()))
        .ToList();
}

// "Mark season watched" lives in the season's "⋯" menu.
static async Task MarkSeasonWatchedAsync(IPage page)
{
    await page.Locator("#seasonMenuButton").ClickAsync();
    await page.GetByRole(AriaRole.Button, new() { Name = "Mark season watched" }).ClickAsync();
}

static async Task OpenFirstEpisodeAsync(IPage page)
{
    await page.Locator("a[href^='/Episodes/Details/']").First.ClickAsync();
    await page.WaitForURLAsync("**/Episodes/Details/**");
    await SettleAsync(page);
}

// Changes the review clone through ../review.sh, which knows the database.
static async Task RunReviewScriptAsync(Options options, params string[] arguments)
{
    if (options.ReviewScript is null)
        throw new InvalidOperationException("This shot needs --review-script (review.sh capture passes it).");

    var start = new System.Diagnostics.ProcessStartInfo("bash") { RedirectStandardError = true };
    start.ArgumentList.Add(options.ReviewScript);
    foreach (var argument in arguments)
        start.ArgumentList.Add(argument);

    using var process = System.Diagnostics.Process.Start(start)!;
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    if (process.ExitCode != 0)
        throw new InvalidOperationException($"review.sh {string.Join(' ', arguments)} failed: {error}");
}

// On a phone the navigation is collapsed behind the toggler.
static async Task OpenMobileMenuAsync(IPage page)
{
    var toggler = page.Locator(".navbar-toggler");
    if (await toggler.IsVisibleAsync())
    {
        await toggler.ClickAsync();
        await page.Locator(".navbar-collapse.show").WaitForAsync();
    }
}

enum Site { SignedIn, Empty, Anonymous }

record Shot(
    string Page,
    string State,
    Site Site,
    string Path,
    Func<IPage, Task>? Prepare = null,
    Func<IPage, Task>? Cleanup = null,
    bool ViewportOnly = false)
{
    public string Name => $"{Page}_{State}";
}

record Viewport(int Width, int Height, float DeviceScaleFactor, bool IsMobile);

record Violation(string Rule, string? Impact, string Help, int Nodes, List<string> Examples, List<string> Messages);

class ShotResult
{
    public string File { get; set; } = "";
    public string Page { get; set; } = "";
    public string State { get; set; } = "";
    public int Width { get; set; }
    public string? Url { get; set; }
    public int Status { get; set; }
    public string? Title { get; set; }
    public List<string> H1 { get; set; } = [];
    public int PageHeight { get; set; }
    public int OverflowPx { get; set; }
    public List<string> TooWide { get; set; } = [];
    public int ImagesWithoutAlt { get; set; }
    public List<string> BrokenImages { get; set; } = [];
    public List<string> ConsoleErrors { get; set; } = [];
    public List<string> FailedRequests { get; set; } = [];
    public List<Violation> Violations { get; set; } = [];
    public string? Error { get; set; }
}

class Options
{
    public string SignedIn { get; private set; } = "http://localhost:7461";
    public string Empty { get; private set; } = "http://localhost:7462";
    public string Anonymous { get; private set; } = "http://localhost:7463";
    public string OutputDirectory { get; private set; } = "screenshots";
    public string? Only { get; private set; }
    public bool Install { get; private set; }
    public string? ReviewScript { get; private set; }

    public string BaseUrl(Site site) => site switch
    {
        Site.SignedIn => SignedIn,
        Site.Empty => Empty,
        _ => Anonymous,
    };

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--signed-in": options.SignedIn = args[++i].TrimEnd('/'); break;
                case "--empty": options.Empty = args[++i].TrimEnd('/'); break;
                case "--anonymous": options.Anonymous = args[++i].TrimEnd('/'); break;
                case "--out": options.OutputDirectory = args[++i]; break;
                case "--only": options.Only = args[++i]; break;
                case "--install": options.Install = true; break;
                case "--review-script": options.ReviewScript = args[++i]; break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        return options;
    }
}
