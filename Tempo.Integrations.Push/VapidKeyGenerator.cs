using System.Security.Cryptography;

namespace Tempo.Integrations.Push;

/// <summary>Generates a VAPID P-256 key pair (RFC 8292) in the base64url form the browser's
/// <c>applicationServerKey</c> and the push services expect. Used once to fill user-secrets,
/// and by tests that need a working signing key.</summary>
public static class VapidKeyGenerator
{
    public static (string PublicKey, string PrivateKey) Generate()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(includePrivateParameters: true);
        var publicKey = new byte[65];
        publicKey[0] = 0x04; // uncompressed EC point
        p.Q.X!.CopyTo(publicKey, 1);
        p.Q.Y!.CopyTo(publicKey, 33);
        return (Base64Url(publicKey), Base64Url(p.D!));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
