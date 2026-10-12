using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SideHub.Cli.Launch;

/// <summary>Writes a CLI's JSON config the launcher changes (trusted folders, hooks), so the CLI never reads half a file.</summary>
public static class ConfigFiles
{
    // Quotes and apostrophes as is: the user may read and edit these files (agy's hooks hold shell commands).
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="root"/>: written to a private temporary file next to it, then
    /// moved over it. Its folder is created when missing.
    /// </summary>
    public static void Replace(string path, JsonObject root)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        var temporary = $"{path}.sidehub-{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
                JsonSerializer.Serialize(stream, root, Options);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
