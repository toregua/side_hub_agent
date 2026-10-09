using System.Text.Json.Serialization;

namespace SideHub.Agent.Models;

public class AgentConnectedMessage
{
    [JsonPropertyName("type")]
    public string Type => "agent.connected";

    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("workspaceId")]
    public required string WorkspaceId { get; init; }

    [JsonPropertyName("capabilities")]
    public required string[] Capabilities { get; init; }

    [JsonPropertyName("defaultShell")]
    public required string DefaultShell { get; init; }

    [JsonPropertyName("availableShells")]
    public required string[] AvailableShells { get; init; }

    [JsonPropertyName("rootPath")]
    public string? RootPath { get; init; }

    [JsonPropertyName("agentVersion")]
    public required string AgentVersion { get; init; }

    /// <summary>
    /// Detected CLI versions keyed by runtime ("claude", "codex", "gemini", "copilot"). Null while not probed yet:
    /// the backend keeps what it knows, and the message is sent again once the probe completes.
    /// </summary>
    [JsonPropertyName("cliVersions")]
    public IReadOnlyDictionary<string, string>? CliVersions { get; init; }

    /// <summary>
    /// Whether each installed CLI is logged in ("claude": false = a run would stop on its login screen). A CLI whose
    /// state is unknown is absent; null while not probed yet, like <see cref="CliVersions"/>.
    /// </summary>
    [JsonPropertyName("cliAuth")]
    public IReadOnlyDictionary<string, bool>? CliAuth { get; init; }

    /// <summary>
    /// The account each CLI runs with, when it tells (only "copilot": source, host, login, plan, sku, organizations,
    /// enterprises). Never a credential; null while not probed yet, like <see cref="CliAuth"/>.
    /// </summary>
    [JsonPropertyName("cliAccounts")]
    public IReadOnlyDictionary<string, CliAccount>? CliAccounts { get; init; }

    /// <summary>The branch <c>origin/HEAD</c> of the agent's repository points to; null when unknown.</summary>
    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; init; }

    /// <summary>Same for the agents sharing this installation (one update restarts them all), see <c>SelfUpdate.InstallId</c>.</summary>
    [JsonPropertyName("installId")]
    public string? InstallId { get; init; }

    /// <summary>"linux", "macos" or "windows".</summary>
    [JsonPropertyName("os")]
    public string? Os { get; init; }

    /// <summary>"x64" or "arm64".</summary>
    [JsonPropertyName("arch")]
    public string? Arch { get; init; }

    /// <summary>Whether the agent can update itself on <c>agent.update</c>, and why not.</summary>
    [JsonPropertyName("selfUpdate")]
    public SelfUpdateInfo? SelfUpdate { get; init; }
}

public class SelfUpdateInfo
{
    [JsonPropertyName("supported")]
    public bool Supported { get; init; }

    /// <summary>"not-installed", "not-writable" or "unsupported-platform" when not supported.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>Progress of an update asked with <c>agent.update</c> (see <c>Update.UpdateStates</c>).</summary>
public class AgentUpdateStatusMessage
{
    [JsonPropertyName("type")]
    public string Type => "agent.update-status";

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>While waiting: what keeps the machine busy ("run", "cli-working", "terminal-activity"…).</summary>
    [JsonPropertyName("busyReasons")]
    public IReadOnlyList<string>? BusyReasons { get; init; }
}

public class AgentHeartbeatMessage
{
    [JsonPropertyName("type")]
    public string Type => "agent.heartbeat";
}
