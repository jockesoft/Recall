using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Recall.Web.Services.Digest;

/// <summary>
/// The token in a digest's unsubscribe link. It says which account the link
/// belongs to and is signed, so it works without signing in and cannot be
/// made up for someone else's account.
/// </summary>
public interface IDigestUnsubscribeTokens
{
    string Create(Guid userId);

    /// <summary>False for anything that is not a token this site issued for this purpose.</summary>
    bool TryRead(string? token, out Guid userId);
}

/// <summary>
/// Signed (and encrypted) with ASP.NET Data Protection under a purpose of its
/// own, so a token issued for anything else is not accepted here and this one
/// is accepted nowhere else. The payload is the user id and nothing more.
///
/// It does not expire: the link in a digest from months ago must still work.
/// It lives as long as the key ring does (the same keys as the sign-in cookie,
/// persisted on the host); if the keys were ever lost, old links stop working
/// and the page tells people to use their profile instead.
/// </summary>
public sealed class DigestUnsubscribeTokens(IDataProtectionProvider dataProtectionProvider) : IDigestUnsubscribeTokens
{
    public const string Purpose = "Recall.Digest.Unsubscribe.v1";

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(Purpose);

    public string Create(Guid userId) =>
        WebEncoders.Base64UrlEncode(_protector.Protect(userId.ToByteArray()));

    public bool TryRead(string? token, out Guid userId)
    {
        userId = Guid.Empty;

        // Far longer than any real token; refuse before doing any work on it.
        if (string.IsNullOrWhiteSpace(token) || token.Length > 512)
            return false;

        try
        {
            var payload = _protector.Unprotect(WebEncoders.Base64UrlDecode(token));
            if (payload.Length != 16)
                return false;

            userId = new Guid(payload);
            return userId != Guid.Empty;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            return false;
        }
    }
}
