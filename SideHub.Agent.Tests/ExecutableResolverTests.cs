namespace SideHub.Agent.Tests;

public class ExecutableResolverTests
{
    private static readonly string[] Fallback = ["/usr/bin", "/bin"];

    [Fact]
    public void Resolves_from_absolute_path_entries_in_order()
    {
        HashSet<string> existing = ["/opt/node/bin/node", "/usr/bin/node"];

        var resolved = ExecutableResolver.ResolveUnix("node", "/opt/node/bin:/usr/bin", Fallback, existing.Contains);

        Assert.Equal("/opt/node/bin/node", resolved);
    }

    [Theory]
    [InlineData(":/usr/local/bin")]
    [InlineData(".:/usr/local/bin")]
    [InlineData("node_modules/.bin:/usr/local/bin")]
    [InlineData("/usr/local/bin::")]
    public void Empty_and_relative_path_entries_are_ignored(string path)
    {
        var probed = new List<string>();

        var resolved = ExecutableResolver.ResolveUnix("git", path, Fallback, p => { probed.Add(p); return p == "/usr/bin/git"; });

        Assert.Equal("/usr/bin/git", resolved);
        Assert.All(probed, p => Assert.StartsWith("/", p));
    }

    [Fact]
    public void Falls_back_to_system_directories_when_path_is_unset()
    {
        Assert.Equal("/bin/git", ExecutableResolver.ResolveUnix("git", null, Fallback, p => p == "/bin/git"));
    }

    [Theory]
    [InlineData("./git")]
    [InlineData("bin/git")]
    [InlineData("")]
    public void Names_carrying_a_path_are_refused(string name)
    {
        Assert.Null(ExecutableResolver.ResolveUnix(name, "/usr/bin", Fallback, _ => true));
    }

    [Fact]
    public void Missing_binary_resolves_to_null()
    {
        Assert.Null(ExecutableResolver.ResolveUnix("node", "/usr/bin", Fallback, _ => false));
    }

    private static readonly string[] WindowsFallback = [@"C:\Windows\system32"];

    [Fact]
    public void Windows_resolves_name_exe_from_fully_qualified_path_entries()
    {
        HashSet<string> existing = [@"C:\Program Files\nodejs\node.exe", @"C:\Windows\system32\cmd.exe"];

        Assert.Equal(@"C:\Program Files\nodejs\node.exe",
            ExecutableResolver.ResolveWindows("node", @"""C:\Program Files\nodejs\"";C:\Windows", WindowsFallback, existing.Contains));
        Assert.Equal(@"C:\Windows\system32\cmd.exe",
            ExecutableResolver.ResolveWindows("cmd.exe", null, WindowsFallback, existing.Contains));
    }

    [Theory]
    [InlineData(@";C:\Tools")]
    [InlineData(@".;C:\Tools")]
    [InlineData(@"bin;C:\Tools")]
    [InlineData(@"\bin;C:\Tools")]
    [InlineData(@"C:bin;C:\Tools")]
    public void Windows_relative_path_entries_are_ignored(string path)
    {
        var probed = new List<string>();

        var resolved = ExecutableResolver.ResolveWindows("git", path, WindowsFallback, p => { probed.Add(p); return p == @"C:\Tools\git.exe"; });

        Assert.Equal(@"C:\Tools\git.exe", resolved);
        Assert.All(probed, p => Assert.Matches(@"^[A-Za-z]:\\", p));
    }

    [Theory]
    [InlineData(@".\git")]
    [InlineData("bin/git")]
    [InlineData("C:git")]
    [InlineData("..")]
    [InlineData("")]
    public void Windows_names_carrying_a_path_are_refused(string name)
    {
        Assert.Null(ExecutableResolver.ResolveWindows(name, @"C:\Windows", WindowsFallback, _ => true));
    }
}

/// <summary>Changes the process-wide working directory: never run alongside other tests.</summary>
[CollectionDefinition(nameof(WorkingDirectoryCollection), DisableParallelization = true)]
public class WorkingDirectoryCollection;

[Collection(nameof(WorkingDirectoryCollection))]
public class BinaryPlantingTests
{
    [Fact]
    public async Task A_git_planted_in_the_repository_is_not_executed()
    {
        if (OperatingSystem.IsWindows() || ExecutableResolver.Resolve("git") is null)
            return;

        var repo = Directory.CreateTempSubdirectory("sidehub-plant-");
        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            var marker = Path.Combine(repo.FullName, "pwned");
            var planted = Path.Combine(repo.FullName, "git");
            await File.WriteAllTextAsync(planted, $"#!/bin/sh\ntouch '{marker}'\n");
            File.SetUnixFileMode(planted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // The daemon runs with the repository as its working directory.
            Directory.SetCurrentDirectory(repo.FullName);
            Assert.NotEqual(planted, ExecutableResolver.Resolve("git"));
            await GitRepository.OpenAsync(repo.FullName);

            Assert.False(File.Exists(marker));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            repo.Delete(recursive: true);
        }
    }
}
