using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SideHub.Agent;

/// <summary>
/// Which plan each CLI of the machine runs on, as a fingerprint (<c>agent.connected.cliAccountKeys</c>, doc 60): a usage
/// limit belongs to the plan's account, not to the agent, so the backend applies one to every agent with the same key.
/// The account itself never leaves the machine: the key is a truncated SHA-256 of the CLI and its identity.
/// <list type="bullet">
/// <item>claude: the organization and e-mail <c>claude auth status</c> prints.</item>
/// <item>copilot: the host and login of its account (<see cref="CopilotAuthProbe"/>).</item>
/// <item>others (codex, agy, gemini) and a claude without them: the machine and the user's home, where the CLI keeps
/// its login (two agents of one machine and user share it).</item>
/// </list>
/// </summary>
public static partial class CliAccountKeys
{
    /// <summary>Hex characters kept from the SHA-256: enough to tell accounts apart, too few to look one up.</summary>
    public const int Length = 16;

    public static string Key(string provider, string identity) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{provider}\n{identity}")))[..Length];

    [GeneratedRegex(@"""orgId""\s*:\s*""([^""]{1,200})""")]
    private static partial Regex OrgIdPattern();

    [GeneratedRegex(@"""email""\s*:\s*""([^""]{1,320})""")]
    private static partial Regex EmailPattern();

    /// <summary>The account of <c>claude auth status</c>'s output (organization and e-mail); null without an e-mail.</summary>
    public static string? ClaudeIdentity(string? statusOutput)
    {
        if (statusOutput is null || EmailPattern().Match(statusOutput) is not { Success: true } email)
            return null;
        var org = OrgIdPattern().Match(statusOutput) is { Success: true } o ? o.Groups[1].Value : "";
        return $"account:{org}\n{email.Groups[1].Value.ToLowerInvariant()}";
    }

    /// <summary>The account of Copilot's probe (host and login); null without a login.</summary>
    public static string? CopilotIdentity(CliAccount? account) =>
        account?.Login is { Length: > 0 } login ? $"account:{account.Host ?? "github.com"}\n{login.ToLowerInvariant()}" : null;

    /// <summary>This machine and user: the machine id (<c>/etc/machine-id</c>, <c>MachineGuid</c> on Windows, else its
    /// name) and the home folder.</summary>
    public static string MachineIdentity(string home) => $"machine:{MachineId()}\n{home}";

    /// <summary>A key for every installed CLI: its account's when known, else the machine's.</summary>
    /// <param name="identities">Account identities known per CLI (<see cref="ClaudeIdentity"/>, <see cref="CopilotIdentity"/>).</param>
    public static Dictionary<string, string> For(
        IEnumerable<string> installed, IReadOnlyDictionary<string, string?> identities, string machineIdentity) =>
        installed.Distinct().ToDictionary(
            cli => cli,
            cli => Key(cli, identities.GetValueOrDefault(cli) ?? machineIdentity));

    private static string MachineId()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null)
                    is string guid && guid.Length > 0)
                    return guid;
            }
            else
            {
                foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
                {
                    if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } id)
                        return id;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Unreadable: the name below
        }
        return Environment.MachineName;
    }
}
