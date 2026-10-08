using System.Security.Cryptography;
using System.Text;

namespace SideHub.Agent.Update;

/// <summary>
/// Checks a release the way install.sh and install.ps1 do: <c>checksums.sha256</c> must carry a valid signature
/// (<c>checksums.sha256.sig</c>, RSA PKCS#1 v1.5 / SHA-256) from the release key, then the archive must match its line.
/// Write access to the GitHub release alone is therefore not enough to ship a tampered archive, and the backend that
/// asks for the update never provides a URL or a hash.
/// </summary>
public static class ReleaseVerifier
{
    /// <summary>The release signing public key. Keep in sync with install.sh and install.ps1 (ReleaseSigningKeyTests).</summary>
    public const string ReleaseSigningKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAt9EUPG9pOyOv9va4UK8v
        I4mJA4nPN7GUiE+Cj7aek6OI3WSl1ma5Bdp7rPYj/aYX+XiM4FMGbzqpLwiYauZz
        Cr4Xlf4Nka/3AKzvl7PorFXCnj1Y5aSw5A5t7loAPEwC6SIQoCvSFmMnDUcbOKje
        t/jhe2aHFk8AoKIhtD1OPP1fYunv9+pmFNYV3RaoTW4KqIvKviJlPOYiJF+eFu1k
        2yXQt9js+hzOBztXIlm3FKR9Te/mzEEbdrZo0SCh4r51yxR/n2Gn4SDlHBqSGxrH
        NqGSMmXNdqsyQuzb6Pu6fYIweRD7gW4gd7bLRDe7bFXHEI2VGlT+5hUEaAfl8Fbo
        oLPHu2P7i+UkiPrDzcehNCQ0LQyyyNEZIxEL63zHWCEsmiXBoRXpaJg94i1Y1Zzm
        nh4Q2DlpTCeVzgUGqgLGUaR4CiJdJRnFQlB+9Rm9UsRIJV5RihAW4R/fEGvBX2Nv
        0WKMxs9stdwcYmYrk19LJGLlFfpL9c5t9TF0KDB2SiIVAgMBAAE=
        -----END PUBLIC KEY-----
        """;

    /// <summary>Throws <see cref="UpdateException"/> (<c>verification-failed</c>) unless the archive is the signed one.</summary>
    public static void Verify(byte[] checksums, byte[] signature, string assetName, Stream archive,
        string publicKeyPem = ReleaseSigningKeyPem)
    {
        if (!IsSignatureValid(checksums, signature, publicKeyPem))
            throw new UpdateException(UpdateErrors.VerificationFailed, "checksums.sha256 is not signed by the release key");

        var expected = ExpectedSha256(Encoding.UTF8.GetString(checksums), assetName)
            ?? throw new UpdateException(UpdateErrors.VerificationFailed, $"{assetName} is not listed in checksums.sha256");
        var actual = Convert.ToHexStringLower(SHA256.HashData(archive));
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(UpdateErrors.VerificationFailed, $"{assetName} does not match its checksum");
    }

    public static bool IsSignatureValid(byte[] data, byte[] signature, string publicKeyPem = ReleaseSigningKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        try
        {
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The hash listed for <paramref name="assetName"/> (<c>sha256sum</c> format: "&lt;hash&gt;  name" or
    /// "&lt;hash&gt; *name"), or null.</summary>
    public static string? ExpectedSha256(string checksums, string assetName)
    {
        foreach (var line in checksums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Trim().TrimStart('*') == assetName)
                return parts[0];
        }
        return null;
    }
}
