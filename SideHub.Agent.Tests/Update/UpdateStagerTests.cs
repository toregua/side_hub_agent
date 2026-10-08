using System.Formats.Tar;
using System.IO.Compression;
using SideHub.Agent.Update;

namespace SideHub.Agent.Tests.Update;

/// <summary>Staging: download from the compiled-in GitHub repository, verify, extract next to the install folder.</summary>
public class UpdateStagerTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly TestRelease _release = new();
    private readonly string _install;

    public UpdateStagerTests()
    {
        _install = _temp.Combine("sidehub-agent");
        Directory.CreateDirectory(_install);
    }

    private FakeReleaseHandler Serve(byte[] archive, byte[]? signature = null)
    {
        var asset = SelfUpdate.AssetName();
        var checksums = _release.Checksums(asset, archive);
        return new FakeReleaseHandler(new()
        {
            [asset] = archive,
            ["checksums.sha256"] = checksums,
            ["checksums.sha256.sig"] = signature ?? _release.Sign(checksums),
        });
    }

    private Task<string> Stage(FakeReleaseHandler handler, string version = "1.0.91", string? tag = null, string? probed = null) =>
        new UpdateStager(handler, _release.PublicKeyPem).StageAsync(_install, version, tag ?? $"v{version}", CancellationToken.None,
            (_, _) => Task.FromResult<string?>(probed ?? version));

    [Fact]
    public async Task A_signed_release_is_extracted_beside_the_install_folder()
    {
        var handler = Serve(TestRelease.ValidArchive());

        var staging = await Stage(handler);

        Assert.Equal($"{_install}.staging-1.0.91", staging);
        Assert.True(File.Exists(Path.Combine(staging, "sidehub-agent")));
        Assert.True(File.Exists(Path.Combine(staging, SelfUpdate.InstallMarker)));
        Assert.All(handler.Requested, url => Assert.StartsWith($"https://github.com/{SelfUpdate.GitHubRepository}/releases/download/v1.0.91/", url));
        Assert.False(File.Exists($"{_install}.download-1.0.91"));
    }

    [Fact]
    public async Task A_badly_signed_release_leaves_nothing_behind()
    {
        using var other = new TestRelease();
        var archive = TestRelease.ValidArchive();
        var handler = Serve(archive, other.Sign(_release.Checksums(SelfUpdate.AssetName(), archive)));

        var error = await Assert.ThrowsAsync<UpdateException>(() => Stage(handler));

        Assert.Equal(UpdateErrors.VerificationFailed, error.Error);
        Assert.Single(Directory.GetFileSystemEntries(_temp.Path));
    }

    [Fact]
    public async Task An_archive_without_node_pty_is_refused()
    {
        var handler = Serve(TestRelease.Archive(new() { ["sidehub-agent"] = "x" }));

        var error = await Assert.ThrowsAsync<UpdateException>(() => Stage(handler));

        Assert.Equal(UpdateErrors.StagingFailed, error.Error);
        Assert.False(Directory.Exists($"{_install}.staging-1.0.91"));
    }

    [Fact]
    public async Task A_binary_reporting_another_version_is_refused()
    {
        var error = await Assert.ThrowsAsync<UpdateException>(() => Stage(Serve(TestRelease.ValidArchive()), probed: "1.0.89"));

        Assert.Equal(UpdateErrors.StagingFailed, error.Error);
    }

    [Fact]
    public async Task A_missing_release_is_a_download_failure()
    {
        var error = await Assert.ThrowsAsync<UpdateException>(() => Stage(new FakeReleaseHandler([])));

        Assert.Equal(UpdateErrors.DownloadFailed, error.Error);
    }

    [Theory]
    [InlineData("1.0.91", "v1.0.92")]
    [InlineData("1.0.91; rm -rf /", "v1.0.91; rm -rf /")]
    [InlineData("../1.0.91", "v../1.0.91")]
    public async Task Only_a_plain_version_and_its_tag_are_accepted(string version, string tag)
    {
        var handler = new FakeReleaseHandler([]);

        var error = await Assert.ThrowsAsync<UpdateException>(() => Stage(handler, version, tag));

        Assert.Equal(UpdateErrors.InvalidRequest, error.Error);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task An_entry_outside_the_destination_is_refused()
    {
        var archive = _temp.Combine("evil.tar.gz");
        await using (var file = File.Create(archive))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var tar = new TarWriter(gzip))
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "../escaped") { DataStream = new MemoryStream([1]) });

        await Assert.ThrowsAsync<IOException>(() => UpdateStager.ExtractAsync(archive, _temp.Combine("out"), CancellationToken.None));
        Assert.False(File.Exists(_temp.Combine("escaped")));
    }

    public void Dispose()
    {
        _release.Dispose();
        _temp.Dispose();
    }
}
