using System.Text.Json.Nodes;

namespace SideHub.Agent.Tests;

/// <summary>
/// A throwaway Codex sessions root; <see cref="AddRollout"/> lays a fixture rollout out the way Codex does
/// (<c>YYYY/MM/DD/rollout-&lt;local time&gt;-&lt;id&gt;.jsonl</c>), its <c>session_meta</c> rewritten for the test.
/// </summary>
public sealed class CodexTemp : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sidehub-codex-tests-" + Guid.NewGuid().ToString("N"));

    public CodexTemp() => Directory.CreateDirectory(Root);

    /// <param name="source">session_meta.source: "cli", "exec", "vscode" (app-server) or a JSON object (sub-agent).</param>
    /// <returns>The rollout's path.</returns>
    public string AddRollout(DateTimeOffset startedAt, string cwd, string fixture = "session.jsonl", string source = "cli")
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "codex", fixture));
        var meta = JsonNode.Parse(lines[0])!;
        var id = Guid.NewGuid().ToString();
        var payload = meta["payload"]!;
        payload["id"] = id;
        payload["timestamp"] = startedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        payload["cwd"] = cwd;
        payload["source"] = source.StartsWith('{') ? JsonNode.Parse(source) : source;
        lines[0] = meta.ToJsonString();

        var dir = Path.Combine(Root, startedAt.ToString("yyyy"), startedAt.ToString("MM"), startedAt.ToString("dd"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"rollout-{startedAt:yyyy-MM-ddTHH-mm-ss}-{id}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
