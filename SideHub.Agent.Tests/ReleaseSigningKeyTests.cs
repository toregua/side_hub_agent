using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SideHub.Agent.Tests;

/// <summary>
/// install.sh (PEM, checked with openssl) and install.ps1 (.NET XML key) each embed the release signing
/// public key: a mismatch would make every signed release uninstallable on one of the platforms.
/// </summary>
public class ReleaseSigningKeyTests
{
    [Fact]
    public void InstallScripts_ShouldEmbedTheSameReleaseSigningKey()
    {
        var scripts = FindScriptsDirectory();
        var bash = File.ReadAllText(Path.Combine(scripts, "install.sh"));
        var powershell = File.ReadAllText(Path.Combine(scripts, "install.ps1"));

        var pem = Regex.Match(bash, "-----BEGIN PUBLIC KEY-----.*?-----END PUBLIC KEY-----", RegexOptions.Singleline);
        var xml = Regex.Match(powershell, "<RSAKeyValue>.*?</RSAKeyValue>");
        Assert.True(pem.Success, "install.sh: release signing key not found");
        Assert.True(xml.Success, "install.ps1: release signing key not found");

        using var fromPem = RSA.Create();
        fromPem.ImportFromPem(pem.Value);
        using var fromXml = RSA.Create();
        fromXml.FromXmlString(xml.Value);

        var expected = fromPem.ExportParameters(false);
        var actual = fromXml.ExportParameters(false);
        Assert.True(expected.Modulus!.Length * 8 >= 3072, "release signing key must be at least RSA-3072");
        Assert.Equal(expected.Modulus, actual.Modulus);
        Assert.Equal(expected.Exponent, actual.Exponent);
    }

    private static string FindScriptsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var scripts = Path.Combine(dir.FullName, "scripts");
            if (File.Exists(Path.Combine(scripts, "install.sh")))
                return scripts;
        }
        throw new DirectoryNotFoundException("scripts/install.sh not found above the test output directory");
    }
}
