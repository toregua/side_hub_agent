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
