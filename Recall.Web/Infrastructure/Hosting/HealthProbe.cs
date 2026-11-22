using System.Globalization;

namespace Recall.Web.Infrastructure.Hosting;

/// <summary>
/// The container's healthcheck: <c>dotnet /app/Recall.Web.dll healthcheck</c>
/// asks the running app's liveness endpoint and exits 0 (healthy) or 1. The
/// hardened runtime image has no shell and no curl, so the app checks itself.
/// </summary>
/// <remarks>
/// It runs before the host is built (<c>Program.cs</c>), so it starts no
/// server, reads no configuration files and connects to nothing but the app.
/// It asks <c>/health/live</c>, which runs no checks: a container must not be
/// marked unhealthy, and restarted by whatever acts on that, because Postgres
/// blinked. One run takes well under a second.
/// </remarks>
public static class HealthProbe
{
    /// <summary>The argument that runs the probe instead of the app.</summary>
    public const string Command = "healthcheck";

    public const string LivePath = "/health/live";

    /// <summary>ASP.NET Core's own default when neither variable says otherwise (and the hardened image's ASPNETCORE_HTTP_PORTS).</summary>
    public const int DefaultPort = 8080;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The port the app listens on for plain HTTP, decided as Kestrel decides
    /// it: <c>ASPNETCORE_URLS</c> wins over <c>ASPNETCORE_HTTP_PORTS</c> when
    /// both are set (Kestrel logs "Overriding HTTP_PORTS"), which matters here
    /// because the image sets HTTP_PORTS=8080 and production sets
    /// URLS=http://+:8701. The first <c>http://</c> address with a usable port
    /// counts; an https-only URLS falls through to HTTP_PORTS.
    /// </summary>
    public static int PortFrom(string? urls, string? httpPorts)
    {
        foreach (var url in Split(urls))
        {
            if (HttpPortOf(url) is { } port)
                return port;
        }

        foreach (var entry in Split(httpPorts))
        {
            if (ValidPort(entry) is { } port)
                return port;
        }

        return DefaultPort;
    }

    /// <summary>Exit code for the answer: 0 for any 2xx, 1 for everything else (a redirect included: it is not followed).</summary>
    public static int ExitCodeFor(int statusCode) => statusCode is >= 200 and <= 299 ? 0 : 1;

    /// <summary>
    /// Asks <c>http://127.0.0.1:{port}/health/live</c> and returns the exit
    /// code; never throws. Writes one line (what docker inspect shows as the
    /// check's output).
    /// </summary>
    public static async Task<int> RunAsync(
        int port, HttpMessageHandler? handler = null, TimeSpan? timeout = null, TextWriter? output = null)
    {
        output ??= Console.Out;
        var uri = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{LivePath}");

        try
        {
            using var client = handler is null
                ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
                : new HttpClient(handler, disposeHandler: false);
            client.Timeout = timeout ?? Timeout;

            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            var code = ExitCodeFor((int)response.StatusCode);
            await output.WriteLineAsync(code == 0
                ? $"healthy: {uri} answered {(int)response.StatusCode}"
                : $"unhealthy: {uri} answered {(int)response.StatusCode}");
            return code;
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"unhealthy: {uri} did not answer ({ex.GetType().Name}: {ex.Message})");
            return 1;
        }
    }

    private static IEnumerable<string> Split(string? list) =>
        (list ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The port of an <c>http://</c> address such as http://+:8701, http://[::]:8701 or http://localhost; null for anything else.</summary>
    private static int? HttpPortOf(string url)
    {
        const string scheme = "http://";
        if (!url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return null;

        var authority = url[scheme.Length..];
        var slash = authority.IndexOf('/');
        if (slash >= 0) authority = authority[..slash];

        // IPv6 hosts are bracketed: the port is what follows "]:".
        var hostEnd = authority.StartsWith('[') ? authority.IndexOf(']') : -1;
        var colon = authority.IndexOf(':', hostEnd + 1);
        return colon < 0 ? 80 : ValidPort(authority[(colon + 1)..]);
    }

    private static int? ValidPort(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : null;
}
