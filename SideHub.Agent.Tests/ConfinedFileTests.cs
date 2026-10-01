namespace SideHub.Agent.Tests;

public class ConfinedFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sidehub-confined-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("sidehub-outside-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_outside, recursive: true); } catch { }
    }

    [Fact]
    public void Write_creates_missing_folders()
    {
        ConfinedFile.WriteAllText(_root, "a/b/c.txt", "hello");

        Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "a", "b", "c.txt")));
    }

    [Fact]
    public void Write_replaces_an_existing_file()
    {
        File.WriteAllText(Path.Combine(_root, "c.txt"), "a much longer previous content");

        ConfinedFile.WriteAllText(_root, "c.txt", "new");

        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "c.txt")));
    }

    [Fact]
    public void Folder_link_is_not_followed_even_inside_the_root()
    {
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), Path.Combine(_root, "real"));

        Assert.Throws<IOException>(() => ConfinedFile.WriteAllText(_root, "alias/c.txt", "x"));
        Assert.False(File.Exists(Path.Combine(_root, "real", "c.txt")));
    }

    [Fact]
    public void Folder_link_leading_outside_is_not_followed()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, ".claude"), _outside);

        Assert.Throws<IOException>(() => ConfinedFile.WriteAllText(_root, ".claude/commands/sidehub.md", "x"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
    }

    [Fact]
    public void File_link_is_not_followed()
    {
        var target = Path.Combine(_outside, "bashrc");
        File.WriteAllText(target, "original");
        File.CreateSymbolicLink(Path.Combine(_root, "AGENTS.md"), target);

        Assert.Throws<IOException>(() => ConfinedFile.WriteAllText(_root, "AGENTS.md", "x"));
        Assert.Equal("original", File.ReadAllText(target));
    }

    [Fact]
    public void Dangling_file_link_is_not_followed()
    {
        var target = Path.Combine(_outside, "not-yet.txt");
        File.CreateSymbolicLink(Path.Combine(_root, "notes.txt"), target);

        Assert.Throws<IOException>(() => ConfinedFile.WriteAllText(_root, "notes.txt", "x"));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Root_reached_through_a_link_is_accepted()
    {
        var link = Path.Combine(_outside, "repo-link");
        Directory.CreateSymbolicLink(link, _root);

        ConfinedFile.WriteAllText(link, "a.txt", "x");

        Assert.Equal("x", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public void CreateNew_refuses_an_existing_file()
    {
        File.WriteAllText(Path.Combine(_root, "1.png"), "first");

        Assert.Throws<IOException>(() => ConfinedFile.Open(_root, "1.png", FileMode.CreateNew, FileAccess.Write));
        Assert.Equal("first", File.ReadAllText(Path.Combine(_root, "1.png")));
    }

    [Fact]
    public void Append_adds_to_the_end()
    {
        File.WriteAllText(Path.Combine(_root, "exclude"), "a\n");

        using (var file = ConfinedFile.Open(_root, "exclude", FileMode.Append, FileAccess.Write))
            file.Write("b\n"u8);

        Assert.Equal("a\nb\n", File.ReadAllText(Path.Combine(_root, "exclude")));
    }

    [Fact]
    public void Open_without_creation_reports_a_missing_file()
    {
        Assert.Throws<FileNotFoundException>(() => ConfinedFile.Open(_root, "missing.txt", FileMode.Open, FileAccess.Read));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData(".")]
    [InlineData("")]
    public void Paths_not_below_the_root_are_rejected(string relativePath)
    {
        Assert.Throws<ArgumentException>(() => ConfinedFile.WriteAllText(_root, relativePath, "x"));
    }

    [Fact]
    public void Read_returns_null_for_a_missing_file()
    {
        Assert.Null(ConfinedFile.ReadAllTextOrNull(_root, "AGENTS.md"));
    }

    [Fact]
    public void Read_follows_a_link_staying_inside()
    {
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "rules");
        File.CreateSymbolicLink(Path.Combine(_root, "AGENTS.md"), Path.Combine(_root, "CLAUDE.md"));

        Assert.Equal("rules", ConfinedFile.ReadAllTextOrNull(_root, "AGENTS.md"));
    }

    [Fact]
    public void Read_refuses_a_link_leading_outside()
    {
        var secret = Path.Combine(_outside, "id_rsa");
        File.WriteAllText(secret, "secret");
        File.CreateSymbolicLink(Path.Combine(_root, "AGENTS.md"), secret);

        Assert.Throws<IOException>(() => ConfinedFile.ReadAllTextOrNull(_root, "AGENTS.md"));
    }
}
