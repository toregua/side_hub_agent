using System.Net.Http.Json;
using System.Text.Json;

namespace SideHub.Cli;

public class SideHubApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _workspaceId;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public string? DefaultAgentId { get; }

    public SideHubApiClient(string apiUrl, string agentToken, string workspaceId, string? defaultAgentId = null)
    {
        _workspaceId = workspaceId;
        DefaultAgentId = string.IsNullOrWhiteSpace(defaultAgentId) ? null : defaultAgentId;
        _http = new HttpClient { BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Add("X-Agent-Token", agentToken);
    }

    // --- Drive ---

    public async Task<JsonElement> GetDriveTreeAsync()
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/drive");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetDriveItemAsync(string itemId)
    {
        var resp = await _http.GetAsync($"api/drive/{itemId}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> CreateDriveItemAsync(string title, string? content, string? parentId, string? type = null)
    {
        var body = new Dictionary<string, object?> { ["title"] = title, ["type"] = type ?? "page" };
        if (content is not null) body["content"] = content;
        if (parentId is not null) body["parentId"] = parentId;

        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/drive", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> UpdateDriveItemAsync(string itemId, string? title, string? content)
    {
        var body = new Dictionary<string, object?>();
        if (title is not null) body["title"] = title;
        if (content is not null) body["content"] = content;

        var resp = await _http.PutAsJsonAsync($"api/drive/{itemId}", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public record DriveDownloadInfo(string FileName, string? MimeType, long? FileSize, string DownloadUrl);

    public async Task<DriveDownloadInfo> GetDriveDownloadInfoAsync(string itemId)
    {
        var item = await GetDriveItemAsync(itemId);

        var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
        var url = item.TryGetProperty("downloadUrl", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(url))
        {
            var label = type is null ? "this item" : $"item of type '{type}'";
            throw new InvalidOperationException($"No downloadable file for {label}. Use `drive read` for text pages.");
        }

        var fileName = item.TryGetProperty("fileName", out var f) ? f.GetString() : null;
        if (string.IsNullOrEmpty(fileName))
            fileName = item.TryGetProperty("title", out var ti) ? ti.GetString() ?? itemId : itemId;

        string? mimeType = item.TryGetProperty("mimeType", out var m) ? m.GetString() : null;
        long? fileSize = item.TryGetProperty("fileSize", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;

        return new DriveDownloadInfo(fileName!, mimeType, fileSize, url!);
    }

    private static readonly HttpClient _downloadClient = new();

    public async Task DownloadToStreamAsync(string presignedUrl, Stream destination, CancellationToken ct = default)
    {
        using var resp = await _downloadClient.GetAsync(presignedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Download failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        await resp.Content.CopyToAsync(destination, ct);
    }

    // --- Tasks ---

    public async Task<JsonElement> GetTasksAsync(string? status)
    {
        var url = $"api/workspaces/{_workspaceId}/tasks";
        if (!string.IsNullOrEmpty(status)) url += $"?status={Uri.EscapeDataString(status)}";

        var resp = await _http.GetAsync(url);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> CreateTaskAsync(string title, string? description, string? type)
    {
        var body = new Dictionary<string, object?> { ["title"] = title };
        if (description is not null) body["description"] = description;
        if (type is not null) body["type"] = type;

        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/tasks", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> AddCommentAsync(string taskId, string text)
    {
        var body = new { text };
        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}/comments", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> AddBlockerAsync(string taskId, string reason)
    {
        var body = new { reason };
        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}/blocker", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // --- Schedulers ---

    public async Task<JsonElement> GetSchedulersAsync(bool? active)
    {
        var url = $"api/workspaces/{_workspaceId}/scheduled-prompts";
        if (active is not null) url += $"?active={active.Value.ToString().ToLowerInvariant()}";

        var resp = await _http.GetAsync(url);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetSchedulerAsync(string id)
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/scheduled-prompts/{id}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetSchedulerExecutionsAsync(string id)
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/scheduled-prompts/{id}/executions");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> CreateSchedulerAsync(string title, string prompt, string cron, string? description, string? provider, string agentId)
    {
        var body = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["prompt"] = prompt,
            ["cronExpression"] = cron,
            ["agentId"] = agentId
        };
        if (description is not null) body["scheduleDescription"] = description;
        if (provider is not null) body["provider"] = provider;

        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/scheduled-prompts", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> UpdateSchedulerAsync(string id, string? title, string? prompt, string? cron, string? description, string? provider, string? agentId)
    {
        var body = new Dictionary<string, object?>();
        if (title is not null) body["title"] = title;
        if (prompt is not null) body["prompt"] = prompt;
        if (cron is not null) body["cronExpression"] = cron;
        if (description is not null) body["scheduleDescription"] = description;
        if (provider is not null) body["provider"] = provider;
        if (agentId is not null) body["agentId"] = agentId;

        var resp = await _http.PutAsJsonAsync($"api/workspaces/{_workspaceId}/scheduled-prompts/{id}", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task DeleteSchedulerAsync(string id)
    {
        var resp = await _http.DeleteAsync($"api/workspaces/{_workspaceId}/scheduled-prompts/{id}");
        await EnsureSuccessAsync(resp);
    }

    public async Task<JsonElement> PauseSchedulerAsync(string id)
    {
        var resp = await _http.PostAsync($"api/workspaces/{_workspaceId}/scheduled-prompts/{id}/pause", null);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> ResumeSchedulerAsync(string id)
    {
        var resp = await _http.PostAsync($"api/workspaces/{_workspaceId}/scheduled-prompts/{id}/resume", null);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> TriggerSchedulerAsync(string id)
    {
        var resp = await _http.PostAsync($"api/workspaces/{_workspaceId}/scheduled-prompts/{id}/trigger", null);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public void Dispose() => _http.Dispose();

    public static string Serialize(JsonElement element) =>
        JsonSerializer.Serialize(element, JsonOptions);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        string? serverMessage = null;
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            if (json.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                serverMessage = msg.GetString();
            else if (json.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                serverMessage = err.GetString();
        }
        catch { /* body is not JSON */ }

        var message = (int)response.StatusCode switch
        {
            401 => serverMessage ?? "Authentication failed. Check your SIDEHUB_AGENT_TOKEN.",
            403 => serverMessage ?? "Access denied.",
            404 => serverMessage ?? "Resource not found.",
            _ => serverMessage ?? $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}"
        };

        throw new HttpRequestException(message);
    }
}
