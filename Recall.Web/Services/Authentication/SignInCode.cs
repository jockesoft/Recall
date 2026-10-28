using System.Security.Cryptography;
using System.Text;

namespace Recall.Web.Services.Authentication;

/// <summary>
/// The 6-digit code in every sign-in email, for signing in where the link
/// does not open: a Home Screen app on an iPhone keeps its own cookies, and
/// the link opens in Safari. It belongs to the same login token as the link.
/// Only a hash is stored, salted with the token's id (so equal codes on two
/// tokens hash differently) and compared in constant time.
/// </summary>
public static class SignInCode
{
    /// <summary>Wrong codes a token survives; the last of them consumes it, and a new email is needed.</summary>
    public const int MaxWrongAttempts = 5;

    public const int Length = 6;

    /// <summary>A uniformly random code, "000000" to "999999".</summary>
    public static string Generate() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"482 913": how the email shows it, in two groups of three.</summary>
    public static string Grouped(string code) => $"{code[..3]} {code[3..]}";

    /// <summary>
    /// The code as typed, without spaces (a pasted "482 913" or "482913 "),
    /// or null when what is left is not six digits.
    /// </summary>
    public static string? Normalize(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
            return null;

        var digits = string.Concat(typed.Where(c => !char.IsWhiteSpace(c)));
        return digits.Length == Length && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    public static string Hash(string code, Guid tokenId) =>
        Convert.ToBase64String(Digest(code, tokenId));

    /// <summary>Whether <paramref name="code"/> is the one hashed into <paramref name="storedHash"/>, in constant time.</summary>
    public static bool Matches(string code, Guid tokenId, string storedHash)
    {
        Span<byte> stored = stackalloc byte[SHA256.HashSizeInBytes];
        return Convert.TryFromBase64String(storedHash, stored, out var written)
               && written == SHA256.HashSizeInBytes
               && CryptographicOperations.FixedTimeEquals(Digest(code, tokenId), stored);
    }

    private static byte[] Digest(string code, Guid tokenId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{tokenId:N}:{code}"));
}
