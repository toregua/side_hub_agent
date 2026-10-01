using SideHub.Cli.Commands;

namespace SideHub.Agent.Tests;

public class DriveDownloadPathTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "sidehub-download-tests");

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("My Report (v2).docx", "My Report (v2).docx")]
    [InlineData("..hidden..txt", "..hidden..txt")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("/etc/cron.d/evil", "evil")]
    [InlineData("..\\..\\evil.bat", "evil.bat")]
    [InlineData("C:\\Windows\\evil.dll", "evil.dll")]
    [InlineData("Q3 2026/summary", "summary")]
    public void Server_name_is_reduced_to_a_file_inside_the_target_directory(string serverName, string expected)
    {
        var path = DriveCommands.ResolveDownloadPath(Dir, serverName);

        Assert.Equal(Path.Combine(Path.GetFullPath(Dir), expected), path);
    }

    [Fact]
    public void Trailing_separator_on_target_directory_is_accepted()
    {
        var path = DriveCommands.ResolveDownloadPath(Dir + Path.DirectorySeparatorChar, "a.txt");

        Assert.Equal(Path.Combine(Path.GetFullPath(Dir), "a.txt"), path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("foo/..")]
    [InlineData("foo/")]
    [InlineData("/")]
    [InlineData("evil\nname.txt")]
    [InlineData("evil\u001b[31m.txt")]
    [InlineData("nul\0byte.txt")]
    public void Unsafe_server_names_are_refused(string? serverName)
    {
        Assert.Throws<InvalidOperationException>(() => DriveCommands.ResolveDownloadPath(Dir, serverName));
    }
}

public class DriveDownloadSaveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-download-save-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Func<Stream, Task> Content(string text) =>
        stream => stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text)).AsTask();

    [Fact]
    public async Task An_existing_link_is_replaced_not_written_through()
    {
        if (OperatingSystem.IsWindows()) return;
        var victim = Path.Combine(_dir, "victim");
        File.WriteAllText(victim, "keep me");
        var target = Path.Combine(_dir, "report.pdf");
        File.CreateSymbolicLink(target, victim);

        var written = await DriveCommands.SaveDownloadAsync(target, Content("downloaded"));

        Assert.Equal(10, written);
        Assert.Equal("keep me", File.ReadAllText(victim));
        Assert.Null(new FileInfo(target).LinkTarget);
        Assert.Equal("downloaded", File.ReadAllText(target));
    }

    [Fact]
    public async Task A_failed_download_leaves_no_partial_file()
    {
        var target = Path.Combine(_dir, "report.pdf");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            DriveCommands.SaveDownloadAsync(target, _ => throw new HttpRequestException("boom")));

        Assert.Empty(Directory.GetFiles(_dir));
    }
}
