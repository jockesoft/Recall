namespace Recall.Web.Infrastructure.Authentication;

/// <summary>
/// Where sign-in returns to, kept on the login token (<c>login_token.return_url</c>)
/// between the request for an email and its redemption. Only a path on this
/// site: the same rule as <c>Url.IsLocalUrl</c> without its "~/" form ("/"
/// followed by anything but a second "/" or "\", no control characters), and
/// at most <see cref="MaxLength"/> characters. The pages check with
/// <c>Url.IsLocalUrl</c> before storing it and again before redirecting.
/// </summary>
public static class LocalReturnUrl
{
    public const int MaxLength = 512;

    /// <summary>The address when it is a local path; null for anything else (absolute, protocol-relative, too long).</summary>
    public static string? Normalize(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > MaxLength || url[0] != '/')
            return null;

        if (url.Length > 1 && url[1] is '/' or '\\')
            return null;

        foreach (var c in url)
        {
            if (char.IsControl(c))
                return null;
        }

        return url;
    }
}
