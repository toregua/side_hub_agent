using SideHub.Cli.Commands;

namespace SideHub.Agent.Tests;

public class DriveUploadMarkdownTests
{
    private const string Id = "3f2b8c1e-0d4a-4b6e-9a7f-1c2d3e4f5a6b";

    [Fact]
    public void Prints_an_image_with_a_drive_reference()
    {
        Assert.Equal($"![Login error](drive:{Id})", DriveCommands.ImageMarkdown("Login error", Id));
    }

    [Theory]
    [InlineData("step [3] failed", @"step \[3\] failed")]
    [InlineData(@"a\b", @"a\\b")]
    [InlineData("two\nlines", "two lines")]
    public void Caption_cannot_break_the_markdown(string caption, string expected)
    {
        Assert.Equal($"![{expected}](drive:{Id})", DriveCommands.ImageMarkdown(caption, Id));
    }
}
