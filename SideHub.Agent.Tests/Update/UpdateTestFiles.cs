using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SideHub.Agent.Tests.Update;

/// <summary>A temporary folder, deleted after the test.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sidehub-update-{Guid.NewGuid():N}");

    public TempFolder() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

/// <summary>A release signed with a key made for the test: archive, checksums.sha256 and its signature.</summary>
public sealed class TestRelease : IDisposable
{
    private readonly RSA _key = RSA.Create(3072);

    public string PublicKeyPem => _key.ExportSubjectPublicKeyInfoPem();

    public static byte[] Archive(Dictionary<string, string> files)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            foreach (var (name, content) in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                };
                tar.WriteEntry(entry);
            }
        }
        return output.ToArray();
    }

    /// <summary>The files install.sh requires: the agent and pty-helper's node-pty.</summary>
    public static byte[] ValidArchive() => Archive(new()
    {
        ["sidehub-agent"] = "#!/bin/sh\necho new",
        ["pty-helper/index.js"] = "",
        ["pty-helper/node_modules/node-pty/package.json"] = "{}",
    });

    public byte[] Checksums(string assetName, byte[] archive) =>
        Encoding.UTF8.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData(archive))}  {assetName}\n0000  other.zip\n");

    public byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    public void Dispose() => _key.Dispose();
}

/// <summary>Answers GitHub release downloads from a dictionary of file name → bytes (404 otherwise).</summary>
public sealed class FakeReleaseHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
{
    public List<string> Requested { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requested.Add(url);
        var name = url[(url.LastIndexOf('/') + 1)..];
        return Task.FromResult(files.TryGetValue(name, out var bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
