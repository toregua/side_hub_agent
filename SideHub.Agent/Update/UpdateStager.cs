using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace SideHub.Agent.Update;

/// <summary>
/// Downloads a release from GitHub, checks it (<see cref="ReleaseVerifier"/>) and extracts it next to the install
/// folder (<c>&lt;install&gt;.staging-&lt;version&gt;</c>, same file system so the swap is a rename). Nothing running is
/// touched: the updater applies it later.
/// </summary>
public sealed partial class UpdateStager(HttpMessageHandler? handler = null, string publicKeyPem = ReleaseVerifier.ReleaseSigningKeyPem)
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    public static string StagingDirectory(string installDirectory, string version) => $"{installDirectory}.staging-{version}";

    /// <summary>Returns the staging folder; throws <see cref="UpdateException"/> on any problem (nothing is left behind).</summary>
    public async Task<string> StageAsync(string installDirectory, string version, string tag, CancellationToken ct,
        Func<string, CancellationToken, Task<string?>>? probeVersion = null)
    {
        if (!VersionPattern().IsMatch(version) || tag != $"v{version}")
            throw new UpdateException(UpdateErrors.InvalidRequest, $"invalid release {tag}");

        var asset = SelfUpdate.AssetName();
        var baseUrl = $"https://github.com/{SelfUpdate.GitHubRepository}/releases/download/{tag}/";
        var staging = StagingDirectory(installDirectory, version);
        var download = $"{installDirectory}.download-{version}";

        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = DownloadTimeout;
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"sidehub-agent/{VersionInfo.AgentVersion}");
        try
        {
            var checksums = await GetBytesAsync(http, baseUrl + "checksums.sha256", ct);
            var signature = await GetBytesAsync(http, baseUrl + "checksums.sha256.sig", ct);
            await DownloadToFileAsync(http, baseUrl + asset, download, ct);

            await using (var archive = File.OpenRead(download))
                ReleaseVerifier.Verify(checksums, signature, asset, archive, publicKeyPem);

            await ExtractAsync(download, staging, ct);
            CheckStaged(staging);
            CopyMode(installDirectory, staging);

            var probed = await (probeVersion ?? ProbeVersionAsync)(Path.Combine(staging, "sidehub-agent"), ct);
            if (probed != version)
                throw new UpdateException(UpdateErrors.StagingFailed, $"the new agent reports version {probed ?? "nothing"}, not {version}");
            return staging;
        }
        catch (Exception ex)
        {
            DeleteDirectory(staging);
            if (ex is UpdateException or OperationCanceledException) throw;
            throw new UpdateException(ex is HttpRequestException ? UpdateErrors.DownloadFailed : UpdateErrors.StagingFailed, ex.Message);
        }
        finally
        {
            try { File.Delete(download); } catch (IOException) { }
        }
    }

    private static async Task<byte[]> GetBytesAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new UpdateException(UpdateErrors.DownloadFailed, $"{Path.GetFileName(url)}: HTTP {(int)response.StatusCode}");
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private static async Task DownloadToFileAsync(HttpClient http, string url, string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new UpdateException(UpdateErrors.DownloadFailed, $"{Path.GetFileName(url)}: HTTP {(int)response.StatusCode}");
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(file, ct);
    }

    /// <summary>
    /// Extracts the release archive. <see cref="TarFile"/> refuses entries and link targets outside the destination,
    /// so a crafted archive cannot write elsewhere (it is signed anyway).
    /// </summary>
    public static async Task ExtractAsync(string archivePath, string destination, CancellationToken ct)
    {
        DeleteDirectory(destination);
        Directory.CreateDirectory(destination);
        await using var file = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: false, ct);
        File.WriteAllText(Path.Combine(destination, SelfUpdate.InstallMarker), "");
    }

    /// <summary>The same checks as install.sh: an archive without the agent or pty-helper's node-pty is refused.</summary>
    public static void CheckStaged(string staging)
    {
        if (!File.Exists(Path.Combine(staging, "sidehub-agent")))
            throw new UpdateException(UpdateErrors.StagingFailed, "sidehub-agent is missing from the archive");
        if (!Directory.Exists(Path.Combine(staging, "pty-helper", "node_modules", "node-pty")))
            throw new UpdateException(UpdateErrors.StagingFailed, "pty-helper/node_modules/node-pty is missing from the archive");
    }

    /// <summary>The install folder keeps its permissions (other users may run a system-wide install).</summary>
    private static void CopyMode(string from, string to)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(to, File.GetUnixFileMode(from));
    }

    /// <summary><c>sidehub-agent version</c> of the staged binary: proves it runs on this machine.</summary>
    private static async Task<string?> ProbeVersionAsync(string executable, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            ArgumentList = { "version" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = Process.Start(psi);
        if (process is null)
            return null;
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* gone */ }
            return null;
        }
    }

    public static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"^\d+(\.\d+){1,3}$")]
    private static partial Regex VersionPattern();
}
