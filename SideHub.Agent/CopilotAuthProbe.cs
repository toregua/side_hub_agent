using System.Text;
using System.Text.Json;

namespace SideHub.Agent;

/// <summary>The account a CLI runs with, as reported to the backend. Never carries a credential.</summary>
/// <param name="Source">How the CLI is authenticated (Copilot: "user", "gh-cli", "env"…, new values passed as is).</param>
/// <param name="Plan">Copilot: <c>copilot_plan</c> ("individual_pro", "business", "enterprise"…).</param>
/// <param name="Sku">Copilot: <c>access_type_sku</c> ("free_limited_copilot", "copilot_for_business_seat_quota"…).</param>
/// <param name="Organizations">Logins of the organizations the account belongs to.</param>
/// <param name="Enterprises">Ids of the enterprises that provide the license (Copilot gives no login for them).</param>
public sealed record CliAccount(
    string Source,
    string? Host,
    string? Login,
    string? Plan,
    string? Sku,
    IReadOnlyList<string> Organizations,
    IReadOnlyList<string> Enterprises)
{
    public bool Equals(CliAccount? other) =>
        other is not null && Source == other.Source && Host == other.Host && Login == other.Login && Plan == other.Plan
        && Sku == other.Sku && Organizations.SequenceEqual(other.Organizations) && Enterprises.SequenceEqual(other.Enterprises);

    public override int GetHashCode() => HashCode.Combine(Source, Host, Login, Plan, Sku);
}

/// <summary>Whether Copilot is logged in, and with which account (null when Copilot did not say).</summary>
public sealed record CopilotAuth(bool LoggedIn, CliAccount? Account);

/// <summary>
/// Asks GitHub Copilot CLI which account it uses. Copilot has no status command; its headless JSON-RPC server (the one
/// <c>@github/copilot-sdk</c> drives) answers <c>account.getCurrentAuth</c> without any credential from us.
/// </summary>
public static class CopilotAuthProbe
{
    public const string CommandLine = "copilot --headless --no-auto-update --stdio";

    private const int MethodNotFound = -32601;
    private const int MaxMessageBytes = 1 << 20;

    /// <summary>
    /// Handshake (<c>connect</c>, or <c>ping</c> for a server that predates it, like the SDK), then
    /// <c>account.getCurrentAuth</c>. Null when the server does not answer them, answers something else, or is slower
    /// than <paramref name="timeout"/>.
    /// </summary>
    public static async Task<CopilotAuth?> QueryAsync(Stream toServer, Stream fromServer, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;
        var reader = new BufferedStream(fromServer);
        try
        {
            var connect = await CallAsync(toServer, reader, 1, "connect", token);
            if (connect is { Error: MethodNotFound })
                connect = await CallAsync(toServer, reader, 2, "ping", token);
            if (connect is not { Error: null })
                return null;

            return await CallAsync(toServer, reader, 3, "account.getCurrentAuth", token) is { Error: null, Result: { } result }
                ? ParseCurrentAuth(result)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// From an <c>AccountGetCurrentAuthResult</c>: an empty or missing <c>authInfo</c> means nobody is logged in. Only
    /// the fields of <see cref="CliAccount"/> are read: the token some variants carry (<c>gh-cli</c>, <c>env</c>) and
    /// the rest of <c>copilotUser</c> are left in the document.
    /// </summary>
    public static CopilotAuth ParseCurrentAuth(JsonElement result)
    {
        if (!result.TryGetProperty("authInfo", out var info) || info.ValueKind != JsonValueKind.Object
            || String(info, "type") is not { Length: > 0 } source)
            return new CopilotAuth(false, null);

        var user = info.TryGetProperty("copilotUser", out var u) && u.ValueKind == JsonValueKind.Object ? u : (JsonElement?)null;
        var organizations = Strings(user, "organization_login_list");
        if (organizations.Count == 0)
            organizations = Items(user, "organization_list").Select(o => String(o, "login")).OfType<string>().ToList();
        var enterprises = Items(user, "enterprise_list")
            .Select(e => e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null)
            .OfType<string>()
            .ToList();

        return new CopilotAuth(true, new CliAccount(
            Source: source,
            Host: String(info, "host"),
            Login: String(info, "login") ?? (user is { } v ? String(v, "login") : null),
            Plan: user is { } p ? String(p, "copilot_plan") : null,
            Sku: user is { } s ? String(s, "access_type_sku") : null,
            Organizations: organizations,
            Enterprises: enterprises));
    }

    private sealed record Response(int? Error, JsonElement? Result);

    private static async Task<Response?> CallAsync(Stream toServer, Stream fromServer, int id, string method, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params = new { } });
        await toServer.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), ct);
        await toServer.WriteAsync(body, ct);
        await toServer.FlushAsync(ct);

        // The server may send notifications (or requests of its own) before answering: skip them.
        while (await ReadMessageAsync(fromServer, ct) is { } message)
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("method", out _)
                || !root.TryGetProperty("id", out var answered) || answered.ValueKind != JsonValueKind.Number
                || answered.GetInt32() != id)
                continue;
            if (root.TryGetProperty("error", out var error))
                return new Response(error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : 0, null);
            return new Response(null, root.TryGetProperty("result", out var result) ? result.Clone() : null);
        }
        return null;
    }

    /// <summary>One <c>Content-Length</c> framed message (vscode-jsonrpc), null at the end of the stream. Lines without
    /// that header (what a login shell profile may print first) are skipped.</summary>
    private static async Task<byte[]?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        int? length = null;
        while (true)
        {
            if (await ReadLineAsync(stream, ct) is not { } line)
                return null;
            if (line.Length == 0)
            {
                if (length is not null)
                    break;
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[(colon + 1)..].Trim(), out var parsed))
                length = parsed;
        }
        if (length is not (> 0 and <= MaxMessageBytes))
            return null;

        var buffer = new byte[length.Value];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, ct) == 0)
                return null;
            if (one[0] == '\n')
                return line.ToString().TrimEnd('\r');
            if (line.Length > 1024)
                return null;
            line.Append((char)one[0]);
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string> Strings(JsonElement? element, string name) =>
        Items(element, name, JsonValueKind.String).Select(e => e.GetString()!).ToList();

    private static IEnumerable<JsonElement> Items(JsonElement? element, string name, JsonValueKind kind = JsonValueKind.Object) =>
        element is { } e && e.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == kind)
            : [];
}
