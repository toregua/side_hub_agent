namespace SideHub.Agent.Tests;

/// <summary>
/// A throwaway Claude projects root; <see cref="AddSession"/> lays the fixture transcript out the way
/// Claude does (<c>&lt;encoded cwd&gt;/&lt;id&gt;.jsonl</c> + <c>&lt;id&gt;/subagents/</c>).
/// </summary>
public sealed class ClaudeTemp : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sidehub-usage-tests-" + Guid.NewGuid().ToString("N"));

    public ClaudeTemp() => Directory.CreateDirectory(Root);

    /// <param name="appendFixtures">Fixture files appended to the session file, in order.</param>
    public void AddSession(string projectDirName, string sessionId, bool withSubagents = true, params string[] appendFixtures)
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude");
        var projectDir = Path.Combine(Root, projectDirName);
        Directory.CreateDirectory(projectDir);
        var sessionFile = Path.Combine(projectDir, sessionId + ".jsonl");
        File.Copy(Path.Combine(fixtures, "session.jsonl"), sessionFile);
        foreach (var extra in appendFixtures)
            File.AppendAllText(sessionFile, "\n" + File.ReadAllText(Path.Combine(fixtures, extra)));

        if (!withSubagents) return;
        var subagents = Path.Combine(projectDir, sessionId, "subagents");
        Directory.CreateDirectory(subagents);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(fixtures, "subagents")))
            File.Copy(file, Path.Combine(subagents, Path.GetFileName(file)));
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
