using Microsoft.Playwright;

namespace Recall.UiReview;

/// <summary>
/// The one way the capture starts a browser. A browser started here never
/// touches the macOS keychain or any other OS credential store, and never a
/// real browser profile:
/// <list type="bullet">
/// <item>Playwright's own bundled engine only: no <c>Channel</c> and no
/// <c>ExecutablePath</c>, so never an installed Chrome, Edge or Brave.</item>
/// <item>A normal launch, never a persistent context: each
/// <c>browser.NewContextAsync()</c> gets a throwaway profile in a temporary
/// folder (review.sh gives each run its own TMPDIR and deletes it, also after
/// a crash or Ctrl-C).</item>
/// <item>Chromium always gets <c>--use-mock-keychain</c> (no "Chromium Safe
/// Storage" keychain item, so no prompt) and <c>--password-store=basic</c>.</item>
/// </list>
/// A launch that breaks a rule fails at once with a message that says which.
/// Calling Playwright's launch methods anywhere else does not build: they are
/// banned in BannedSymbols.txt. A keychain prompt during a run is a bug to fix
/// here, never something to approve.
/// </summary>
public static class BrowserLaunch
{
    public const string MockKeychain = "--use-mock-keychain";
    public const string BasicPasswordStore = "--password-store=basic";

    /// <summary>Arguments that would point a normal launch at a profile on disk.</summary>
    private static readonly string[] ProfileArguments = ["--user-data-dir", "--profile-directory"];

    /// <summary>Launches <paramref name="browserType"/> (Chromium, and WebKit or Firefox if ever used) under the rules above.</summary>
    public static Task<IBrowser> LaunchAsync(IBrowserType browserType, BrowserTypeLaunchOptions? options = null)
    {
        options ??= new BrowserTypeLaunchOptions();

        if (browserType.Name == BrowserType.Chromium)
            options.Args = (options.Args ?? []).Concat([MockKeychain, BasicPasswordStore]).Distinct().ToList();

        EnsureSafe(browserType.Name, options);

#pragma warning disable RS0030 // The one place a browser is launched; everything else goes through here.
        return browserType.LaunchAsync(options);
#pragma warning restore RS0030
    }

    /// <summary>Throws, saying why, when <paramref name="options"/> would break a rule; the launch above calls it last.</summary>
    public static void EnsureSafe(string browserName, BrowserTypeLaunchOptions options)
    {
#pragma warning disable RS0030 // Reading the banned settings to refuse them.
        if (!string.IsNullOrEmpty(options.Channel))
            throw Refused($"it names the browser channel \"{options.Channel}\" (an installed Chrome, Edge or Brave). Use Playwright's bundled {browserName}.");

        if (!string.IsNullOrEmpty(options.ExecutablePath))
            throw Refused($"it names an executable ({options.ExecutablePath}). Use Playwright's bundled {browserName}.");
#pragma warning restore RS0030

        var args = options.Args?.ToList() ?? [];

        if (args.FirstOrDefault(a => ProfileArguments.Any(p => a.StartsWith(p, StringComparison.Ordinal))) is { } profile)
            throw Refused($"it points the browser at a profile on disk ({profile}), which is a persistent context. Launch normally and use a new context.");

        if (browserName == BrowserType.Chromium)
        {
            var missing = new[] { MockKeychain, BasicPasswordStore }.Where(flag => !args.Contains(flag)).ToList();
            if (missing.Count > 0)
                throw Refused($"Chromium lacks {string.Join(" and ", missing)}, without which it may ask the macOS keychain for \"Chromium Safe Storage\".");
        }
    }

    /// <summary>
    /// The guard's own check, for <c>--check-launch-guard</c>: every unsafe
    /// launch above is refused, and a plain one passes with both flags added.
    /// Starts no browser. Returns the failures (none when the guard holds).
    /// </summary>
    public static IReadOnlyList<string> SelfCheck()
    {
        var failures = new List<string>();

        void MustRefuse(string what, string browserName, BrowserTypeLaunchOptions options)
        {
            try
            {
                EnsureSafe(browserName, options);
                failures.Add($"not refused: {what}");
            }
            catch (InvalidOperationException)
            {
            }
        }

#pragma warning disable RS0030 // Setting the banned settings, to show they are refused.
        MustRefuse("a channel", BrowserType.Chromium, new() { Channel = "chrome", Args = [MockKeychain, BasicPasswordStore] });
        MustRefuse("an executable path", BrowserType.Chromium,
            new() { ExecutablePath = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", Args = [MockKeychain, BasicPasswordStore] });
        MustRefuse("a channel for WebKit", BrowserType.Webkit, new() { Channel = "webkit" });
#pragma warning restore RS0030
        MustRefuse("a profile folder", BrowserType.Chromium, new() { Args = ["--user-data-dir=/tmp/profile", MockKeychain, BasicPasswordStore] });
        MustRefuse("Chromium without --use-mock-keychain", BrowserType.Chromium, new() { Args = [BasicPasswordStore] });
        MustRefuse("Chromium without --password-store=basic", BrowserType.Chromium, new() { Args = [MockKeychain] });

        try
        {
            EnsureSafe(BrowserType.Chromium, new() { Args = [MockKeychain, BasicPasswordStore] });
        }
        catch (InvalidOperationException ex)
        {
            failures.Add($"a safe launch was refused: {ex.Message}");
        }

        return failures;
    }

    private static InvalidOperationException Refused(string why) =>
        new($"Browser launch refused: {why} Browser automation in Recall never touches the keychain or a real profile (see BrowserLaunch).");
}
