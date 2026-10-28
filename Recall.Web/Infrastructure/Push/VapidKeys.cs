using System.Buffers.Text;
using System.Security.Cryptography;

namespace Recall.Web.Infrastructure.Push;

/// <summary>
/// The VAPID key pair: a P-256 key whose public half browsers are given when
/// they subscribe, and whose private half signs every push, so a push service
/// only accepts pushes for a subscription from the server that created it.
/// </summary>
public static class VapidKeys
{
    public sealed record KeyPair(string PublicKey, string PrivateKey);

    /// <summary>A new key pair, in the base64url form <see cref="PushOptions"/> takes.</summary>
    public static KeyPair Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);

        return new KeyPair(
            Base64Url.EncodeToString(UncompressedPoint(parameters.Q)),
            Base64Url.EncodeToString(parameters.D!));
    }

    /// <summary>
    /// What is wrong with the push settings, in words that say what to set, or
    /// <c>null</c> when they can be used. Only meaningful when push is enabled.
    /// </summary>
    public static string? Problem(PushOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PublicKey) || string.IsNullOrWhiteSpace(options.PrivateKey))
            return "Push:PublicKey and Push:PrivateKey must both be set.";

        if (!TryDecode(options.PublicKey, 65, out var publicKey) || publicKey[0] != 0x04)
            return "Push:PublicKey is not a P-256 public key in base64url (87 characters).";

        if (!TryDecode(options.PrivateKey, 32, out var privateKey))
            return "Push:PrivateKey is not a P-256 private key in base64url (43 characters).";

        if (!Matches(publicKey, privateKey))
            return "Push:PublicKey and Push:PrivateKey are not the two halves of one key pair.";

        var subject = (options.Subject ?? string.Empty).Trim();
        if (!subject.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || !subject.Contains('@'))
            return "Push:Subject must be a mailto: address (for example mailto:admin@recall.nu).";

        return null;
    }

    private static bool TryDecode(string value, int length, out byte[] bytes)
    {
        bytes = [];
        try
        {
            bytes = Base64Url.DecodeFromChars(value.Trim());
            return bytes.Length == length;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Signs with the private key and checks the signature with the public key
    /// alone: a pair that does not belong together would have every push
    /// rejected by the push services, and nothing else would say why.
    /// </summary>
    private static bool Matches(byte[] publicKey, byte[] privateKey)
    {
        var point = new ECPoint { X = publicKey[1..33], Y = publicKey[33..] };

        try
        {
            using var signer = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = privateKey, Q = point });
            using var verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = point });

            byte[] probe = [1, 2, 3, 4];
            return verifier.VerifyData(probe, signer.SignData(probe, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] UncompressedPoint(ECPoint point) => [0x04, .. point.X!, .. point.Y!];
}
