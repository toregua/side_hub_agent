namespace SideHub.Agent.Tests;

public class FileWritePolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sidehub-filewrite-{Guid.NewGuid():N}");
    private readonly string _outside = Directory.CreateTempSubdirectory("sidehub-outside-").FullName;

    public FileWritePolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_outside, recursive: true); } catch { }
    }

    private bool Resolve(string requested, out string resolved) =>
        FileWritePolicy.TryResolveTarget(_root, requested, out resolved, out _);

    [Theory]
    [InlineData("image.png")]
    [InlineData("docs/sub/image.png")]
    [InlineData(".sidehub-images/1.png")]
    [InlineData(".github/workflows/ci.yml")]
    public void Files_inside_the_working_directory_are_accepted(string requested)
    {
        Assert.True(Resolve(requested, out var resolved));
        Assert.Equal(Path.Combine(_root, requested), resolved);
    }

    [Fact]
    public void Absolute_path_inside_the_working_directory_is_accepted()
    {
        var path = Path.Combine(_root, "a.txt");

        Assert.True(Resolve(path, out var resolved));
        Assert.Equal(path, resolved);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData(".")]
    public void Paths_outside_the_working_directory_are_rejected(string requested)
    {
        Assert.False(Resolve(requested, out _));
    }

    [Fact]
    public void Sibling_with_the_root_as_a_name_prefix_is_rejected()
    {
        Assert.False(Resolve(_root + "-other/a.txt", out _));
    }

    [Theory]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData(".git/config")]
    [InlineData(".git")]
    [InlineData(".GIT/hooks/post-checkout")]
    [InlineData("vendor/lib/.git/hooks/pre-commit")]
    [InlineData(".sidehub/agent.json")]
    [InlineData(".sidehub/run/sidehub-agent.pid")]
    [InlineData("sub/../.sidehub/agent.json")]
    public void Protected_folders_are_rejected(string requested)
    {
        Assert.False(FileWritePolicy.TryResolveTarget(_root, requested, out _, out var error));
        Assert.Contains("protected", error);
    }

    [Fact]
    public void Folder_link_leading_outside_is_rejected()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), _outside);

        Assert.False(FileWritePolicy.TryResolveTarget(_root, "escape/payload.sh", out _, out var error));
        Assert.Contains("outside", error);
    }

    [Fact]
    public void File_link_leading_outside_is_rejected()
    {
        var target = Path.Combine(_outside, "bashrc");
        File.WriteAllText(target, "");
        File.CreateSymbolicLink(Path.Combine(_root, "notes.txt"), target);

        Assert.False(Resolve("notes.txt", out _));
    }

    [Fact]
    public void Dangling_link_leading_outside_is_rejected()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "notes.txt"), Path.Combine(_outside, "not-yet.txt"));

        Assert.False(Resolve("notes.txt", out _));
    }

    [Fact]
    public void Link_leading_into_a_protected_folder_is_rejected()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "hooks"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "hooks"), Path.Combine(_root, ".git", "hooks"));

        Assert.False(FileWritePolicy.TryResolveTarget(_root, "hooks/pre-commit", out _, out var error));
        Assert.Contains("protected", error);
    }

    [Fact]
    public void Link_staying_inside_resolves_to_its_target()
    {
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), Path.Combine(_root, "real"));

        Assert.True(Resolve("alias/a.png", out var resolved));
        Assert.Equal(Path.Combine(_root, "real", "a.png"), resolved);
    }

    [Fact]
    public void Link_loop_is_rejected()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "a"), Path.Combine(_root, "b"));
        File.CreateSymbolicLink(Path.Combine(_root, "b"), Path.Combine(_root, "a"));

        Assert.False(Resolve("a/x.txt", out _));
    }

    [Fact]
    public void Working_directory_reached_through_a_link_is_accepted()
    {
        var link = Path.Combine(_outside, "repo-link");
        Directory.CreateSymbolicLink(link, _root);

        Assert.True(FileWritePolicy.TryResolveTarget(link, "a.txt", out var resolved, out _));
        Assert.Equal(Path.Combine(_root, "a.txt"), resolved);
    }

    [Theory]
    [InlineData(".claude/settings.json")]
    [InlineData(".claude/settings.local.json")]
    [InlineData("sub/.CLAUDE/Settings.json")]
    [InlineData(".mcp.json")]
    [InlineData(".envrc")]
    [InlineData("packages/api/.envrc")]
    [InlineData(".vscode/tasks.json")]
    [InlineData(".gemini/settings.json")]
    [InlineData(".g\u200Cit/hooks/pre-commit")]
    [InlineData(".git./config")]
    public void Tool_configuration_files_and_aliases_of_protected_folders_are_rejected(string requested)
    {
        Assert.False(FileWritePolicy.TryResolveTarget(_root, requested, out _, out var error));
        Assert.Contains("protected", error);
    }

    [Theory]
    [InlineData(".claude/commands/sidehub.md")]
    [InlineData("AGENTS.md")]
    [InlineData("docs/settings.json")]
    public void Generated_files_are_not_protected(string requested)
    {
        Assert.True(Resolve(requested, out _));
    }

    [Theory]
    [InlineData("GIT~1/hooks/pre-commit", true)]
    [InlineData("SIDEHU~1/agent.json", true)]
    [InlineData("PROGRA~1.TXT", true)]
    [InlineData(".git::$INDEX_ALLOCATION/hooks/pre-commit", true)]
    [InlineData("image.png:stream", true)]
    [InlineData(".sidehub-images/1.png", false)]
    [InlineData("notes~draft.md", false)]
    public void Windows_short_names_and_data_streams_are_detected(string relativePath, bool alias)
    {
        Assert.Equal(alias, FileWritePolicy.HasWindowsAlias(relativePath));
    }

    [Fact]
    public void Link_swapped_in_after_the_check_does_not_redirect_the_write()
    {
        Directory.CreateDirectory(Path.Combine(_root, "uploads"));
        Assert.True(Resolve("uploads/a.png", out var resolved));

        Directory.Delete(Path.Combine(_root, "uploads"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "uploads"), _outside);

        Assert.Throws<IOException>(() =>
            FileWritePolicy.OpenWrite(_root, Path.GetRelativePath(_root, resolved), FileMode.Create, out _));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
    }

    [Fact]
    public void OpenWrite_refuses_protected_paths()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            FileWritePolicy.OpenWrite(_root, ".git/info/exclude", FileMode.Append, out _));
        Assert.Throws<UnauthorizedAccessException>(() =>
            FileWritePolicy.OpenWrite(_root, ".claude/settings.json", FileMode.Create, out _));
    }

    [Fact]
    public void OpenWrite_reports_the_full_path()
    {
        using (FileWritePolicy.OpenWrite(_root, ".sidehub-images/1.png", FileMode.CreateNew, out var fullPath))
            Assert.Equal(Path.Combine(_root, ".sidehub-images", "1.png"), fullPath);
    }
}
