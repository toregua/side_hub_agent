using System.Text;
using SideHub.Agent.Update;

namespace SideHub.Agent.Tests.Update;

/// <summary>The agent checks a release exactly like install.sh: signed checksums, then the archive's hash.</summary>
public class ReleaseVerifierTests : IDisposable
{
    private const string Asset = "sidehub-agent-linux-x64.tar.gz";
    private readonly TestRelease _release = new();

    [Fact]
    public void A_signed_release_is_accepted()
    {
        var archive = TestRelease.ValidArchive();
        var checksums = _release.Checksums(Asset, archive);

        ReleaseVerifier.Verify(checksums, _release.Sign(checksums), Asset, new MemoryStream(archive), _release.PublicKeyPem);
    }

    [Fact]
    public void Checksums_signed_by_another_key_are_refused()
    {
        using var other = new TestRelease();
        var archive = TestRelease.ValidArchive();
        var checksums = _release.Checksums(Asset, archive);

        var error = Assert.Throws<UpdateException>(() =>
            ReleaseVerifier.Verify(checksums, other.Sign(checksums), Asset, new MemoryStream(archive), _release.PublicKeyPem));
        Assert.Equal(UpdateErrors.VerificationFailed, error.Error);
    }

    [Fact]
    public void A_tampered_archive_is_refused()
    {
        var archive = TestRelease.ValidArchive();
        var checksums = _release.Checksums(Asset, archive);
        archive[^1] ^= 0xFF;

        var error = Assert.Throws<UpdateException>(() =>
            ReleaseVerifier.Verify(checksums, _release.Sign(checksums), Asset, new MemoryStream(archive), _release.PublicKeyPem));
        Assert.Contains("does not match", error.Message);
    }

    [Fact]
    public void An_archive_missing_from_the_checksums_is_refused()
    {
        var archive = TestRelease.ValidArchive();
        var checksums = _release.Checksums("sidehub-agent-osx-arm64.tar.gz", archive);

        var error = Assert.Throws<UpdateException>(() =>
            ReleaseVerifier.Verify(checksums, _release.Sign(checksums), Asset, new MemoryStream(archive), _release.PublicKeyPem));
        Assert.Contains("not listed", error.Message);
    }

    [Fact]
    public void A_garbage_signature_is_refused_without_throwing_a_crypto_error()
    {
        var checksums = Encoding.UTF8.GetBytes("abc  x\n");

        Assert.False(ReleaseVerifier.IsSignatureValid(checksums, [1, 2, 3], _release.PublicKeyPem));
    }

    [Theory]
    [InlineData("abc123  sidehub-agent-linux-x64.tar.gz", "abc123")]
    [InlineData("abc123 *sidehub-agent-linux-x64.tar.gz", "abc123")]
    [InlineData("abc123  sidehub-agent-linux-x64.tar.gz.sig", null)]
    [InlineData("abc123  other-sidehub-agent-linux-x64.tar.gz", null)]
    public void The_hash_is_read_from_sha256sum_lines(string line, string? expected) =>
        Assert.Equal(expected, ReleaseVerifier.ExpectedSha256($"0000  x\n{line}\n", Asset));

    public void Dispose() => _release.Dispose();
}
