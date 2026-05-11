using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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

    public async Task DeleteDriveItemAsync(string itemId)
    {
        var resp = await _http.DeleteAsync($"api/drive/{itemId}");
        await EnsureSuccessAsync(resp);
    }

    public async Task<JsonElement> MoveDriveItemAsync(string itemId, string? newParentId, string? afterSiblingId)
    {
        var body = new Dictionary<string, object?>
        {
            ["newParentId"] = newParentId
        };
        if (afterSiblingId is not null) body["afterSiblingId"] = afterSiblingId;

        var resp = await _http.PutAsJsonAsync($"api/drive/{itemId}/move", body);
        await EnsureSuccessAsync(resp);
        var content = await resp.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(content) ? default : JsonSerializer.Deserialize<JsonElement>(content);
    }

    public async Task<JsonElement> UploadFileAsync(string localPath, string? parentId, string? title)
    {
        if (!File.Exists(localPath))
            throw new InvalidOperationException($"File not found: {localPath}");

        using var content = new MultipartFormDataContent();
        await using var fs = File.OpenRead(localPath);
        var fileContent = new StreamContent(fs);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessMimeType(localPath));
        content.Add(fileContent, "file", Path.GetFileName(localPath));

        var url = $"api/workspaces/{_workspaceId}/drive/upload";
        var query = new List<string>();
        if (!string.IsNullOrEmpty(parentId)) query.Add($"parentId={Uri.EscapeDataString(parentId)}");
        if (!string.IsNullOrEmpty(title)) query.Add($"title={Uri.EscapeDataString(title)}");
        if (query.Count > 0) url += "?" + string.Join("&", query);

        var resp = await _http.PostAsync(url, content);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<string> GetDriveJsonAsync(string itemId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"api/drive/{itemId}/json", ct);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> ReplaceDriveJsonAsync(string itemId, string jsonContent, CancellationToken ct = default)
    {
        using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        var resp = await _http.PutAsync($"api/drive/{itemId}/json", content, ct);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> PatchDriveJsonAsync(string itemId, string patchJsonArray, CancellationToken ct = default)
    {
        using var content = new StringContent(patchJsonArray, Encoding.UTF8, "application/json-patch+json");
        var req = new HttpRequestMessage(HttpMethod.Patch, $"api/drive/{itemId}/json") { Content = content };
        var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<JsonElement> UploadJsonBytesAsync(string fileName, byte[] bytes, string? parentId, string? title, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(fileContent, "file", fileName);

        var url = $"api/workspaces/{_workspaceId}/drive/upload";
        var query = new List<string>();
        if (!string.IsNullOrEmpty(parentId)) query.Add($"parentId={Uri.EscapeDataString(parentId)}");
        if (!string.IsNullOrEmpty(title)) query.Add($"title={Uri.EscapeDataString(title)}");
        if (query.Count > 0) url += "?" + string.Join("&", query);

        var resp = await _http.PostAsync(url, content, ct);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    public async Task<JsonElement> GetRecentDriveItemsAsync(int? limit)
    {
        var url = "api/drive/recent";
        var query = new List<string>();
        if (limit is not null) query.Add($"limit={limit.Value}");
        // Scope to current workspace for relevance.
        query.Add($"workspaceId={_workspaceId}");
        url += "?" + string.Join("&", query);

        var resp = await _http.GetAsync(url);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetStorageUsageAsync()
    {
        var resp = await _http.GetAsync("api/drive/storage/usage");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // --- SQLite ---

    public async Task<JsonElement> SqliteQueryAsync(
        string itemId, string sql, IReadOnlyList<object?> parameters, int? rowLimit, int? timeoutSeconds,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["sql"] = sql,
            ["parameters"] = parameters,
        };
        if (rowLimit is not null) body["rowLimit"] = rowLimit.Value;
        if (timeoutSeconds is not null) body["timeoutSeconds"] = timeoutSeconds.Value;

        var resp = await _http.PostAsJsonAsync($"api/drive/{itemId}/sqlite/query", body, ct);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    public async Task<JsonElement> SqliteExecAsync(
        string itemId, string sql, IReadOnlyList<object?> parameters, bool allowDdl, int? timeoutSeconds,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["sql"] = sql,
            ["parameters"] = parameters,
            ["allowDdl"] = allowDdl,
        };
        if (timeoutSeconds is not null) body["timeoutSeconds"] = timeoutSeconds.Value;

        var resp = await _http.PostAsJsonAsync($"api/drive/{itemId}/sqlite/exec", body, ct);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    public async Task<JsonElement> SqliteSchemaAsync(string itemId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"api/drive/{itemId}/sqlite/schema", ct);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    public async Task<JsonElement> CreateSqliteDatabaseAsync(
        string title, string? initialSchemaSql, string? parentId, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["title"] = title };
        if (!string.IsNullOrEmpty(initialSchemaSql)) body["initialSchemaSql"] = initialSchemaSql;
        if (!string.IsNullOrEmpty(parentId)) body["parentId"] = parentId;

        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/drive/sqlite", body, ct);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    private static string GuessMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        ".txt" or ".md" => "text/plain",
        ".json" => "application/json",
        ".csv" => "text/csv",
        ".zip" => "application/zip",
        ".sqlite" or ".sqlite3" or ".db" => "application/vnd.sqlite3",
        _ => "application/octet-stream"
    };

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

    public async Task<JsonElement> GetTaskAsync(string taskId)
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> UpdateTaskAsync(string taskId, string? title, string? description, string? type)
    {
        var body = new Dictionary<string, object?>();
        if (title is not null) body["title"] = title;
        if (description is not null) body["description"] = description;
        if (type is not null) body["type"] = type;

        var resp = await _http.PutAsJsonAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task DeleteTaskAsync(string taskId)
    {
        var resp = await _http.DeleteAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}");
        await EnsureSuccessAsync(resp);
    }

    public async Task<JsonElement> SetTaskStatusAsync(string taskId, string status)
    {
        var body = new { status };
        var req = new HttpRequestMessage(HttpMethod.Patch, $"api/workspaces/{_workspaceId}/tasks/{taskId}/status")
        {
            Content = JsonContent.Create(body)
        };
        var resp = await _http.SendAsync(req);
        await EnsureSuccessAsync(resp);
        var content = await resp.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(content) ? default : JsonSerializer.Deserialize<JsonElement>(content);
    }

    public async Task<JsonElement> AddTaskDriveLinkAsync(string taskId, string driveItemId)
    {
        var resp = await _http.PostAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}/drive-links/{driveItemId}", null);
        await EnsureSuccessAsync(resp);
        var content = await resp.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(content) ? default : JsonSerializer.Deserialize<JsonElement>(content);
    }

    public async Task RemoveTaskDriveLinkAsync(string taskId, string driveItemId)
    {
        var resp = await _http.DeleteAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}/drive-links/{driveItemId}");
        await EnsureSuccessAsync(resp);
    }

    public async Task ResolveTaskBlockerAsync(string taskId)
    {
        var resp = await _http.DeleteAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}/blocker");
        await EnsureSuccessAsync(resp);
    }

    public async Task<JsonElement> SearchTasksAsync(string query, string? status, string? type)
    {
        var url = $"api/tasks/search?query={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrEmpty(status)) url += $"&status={Uri.EscapeDataString(status)}";
        if (!string.IsNullOrEmpty(type)) url += $"&type={Uri.EscapeDataString(type)}";

        var resp = await _http.GetAsync(url);
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

    public async Task<JsonElement> CreateSchedulerAsync(string title, string? prompt, string cron, string? description, string? provider, string agentId, string? workflowId)
    {
        var body = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["cronExpression"] = cron,
            ["agentId"] = agentId
        };
        if (prompt is not null) body["prompt"] = prompt;
        if (workflowId is not null) body["workflowId"] = workflowId;
        if (description is not null) body["scheduleDescription"] = description;
        if (provider is not null) body["provider"] = provider;

        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/scheduled-prompts", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> UpdateSchedulerAsync(string id, string? title, string? prompt, string? cron, string? description, string? provider, string? agentId, string? workflowId)
    {
        var body = new Dictionary<string, object?>();
        if (title is not null) body["title"] = title;
        if (prompt is not null) body["prompt"] = prompt;
        if (cron is not null) body["cronExpression"] = cron;
        if (description is not null) body["scheduleDescription"] = description;
        if (provider is not null) body["provider"] = provider;
        if (agentId is not null) body["agentId"] = agentId;
        if (workflowId is not null) body["workflowId"] = workflowId;

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

    // --- Workflow executions ---

    public async Task<JsonElement> CompleteWorkflowStepAsync(string executionId, string stepId, string outputDriveItemId)
    {
        var body = new { outputDriveItemId };
        var resp = await _http.PostAsJsonAsync($"api/workflow-executions/{executionId}/steps/{stepId}/complete", body);
        await EnsureSuccessAsync(resp);
        var content = await resp.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(content) ? default : JsonSerializer.Deserialize<JsonElement>(content);
    }

    public async Task<JsonElement> FailWorkflowStepAsync(string executionId, string stepId, string reason)
    {
        var body = new { reason };
        var resp = await _http.PostAsJsonAsync($"api/workflow-executions/{executionId}/steps/{stepId}/fail", body);
        await EnsureSuccessAsync(resp);
        var content = await resp.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(content) ? default : JsonSerializer.Deserialize<JsonElement>(content);
    }

    // --- Workflows ---

    public async Task<JsonElement> ListWorkflowsAsync()
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/workflows");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetWorkflowAsync(string workflowId)
    {
        var resp = await _http.GetAsync($"api/workflows/{workflowId}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> CreateWorkflowAsync(string name, string? description, int? defaultStepTimeoutMinutes)
    {
        var body = new Dictionary<string, object?> { ["name"] = name };
        if (description is not null) body["description"] = description;
        if (defaultStepTimeoutMinutes is not null) body["defaultStepTimeoutMinutes"] = defaultStepTimeoutMinutes;

        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/workflows", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> UpdateWorkflowAsync(string workflowId, string? name, string? description, int? defaultStepTimeoutMinutes, bool clearDefaultStepTimeout)
    {
        var body = new Dictionary<string, object?>();
        if (name is not null) body["name"] = name;
        if (description is not null) body["description"] = description;
        if (defaultStepTimeoutMinutes is not null) body["defaultStepTimeoutMinutes"] = defaultStepTimeoutMinutes;
        if (clearDefaultStepTimeout) body["clearDefaultStepTimeout"] = true;

        var req = new HttpRequestMessage(HttpMethod.Patch, $"api/workflows/{workflowId}")
        {
            Content = JsonContent.Create(body)
        };
        var resp = await _http.SendAsync(req);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task DeleteWorkflowAsync(string workflowId)
    {
        var resp = await _http.DeleteAsync($"api/workflows/{workflowId}");
        await EnsureSuccessAsync(resp);
    }

    public sealed record StepInputArg(string Kind, string Id);

    public async Task<JsonElement> AddWorkflowStepAsync(
        string workflowId, string name, string prompt, IEnumerable<StepInputArg> inputs,
        string outputPath, int? timeoutMinutes, string? outputDriveItemId)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["prompt"] = prompt,
            ["inputs"] = inputs.Select(SerializeInput).ToArray(),
            ["outputPath"] = outputPath
        };
        if (timeoutMinutes is not null) body["timeoutMinutes"] = timeoutMinutes;
        if (outputDriveItemId is not null) body["outputDriveItemId"] = outputDriveItemId;

        var resp = await _http.PostAsJsonAsync($"api/workflows/{workflowId}/steps", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> UpdateWorkflowStepAsync(
        string workflowId, string stepId,
        string? name, string? prompt, IEnumerable<StepInputArg>? inputs,
        string? outputPath, int? timeoutMinutes, string? outputDriveItemId, bool clearOutputDriveItemId)
    {
        var body = new Dictionary<string, object?>();
        if (name is not null) body["name"] = name;
        if (prompt is not null) body["prompt"] = prompt;
        if (inputs is not null) body["inputs"] = inputs.Select(SerializeInput).ToArray();
        if (outputPath is not null) body["outputPath"] = outputPath;
        if (timeoutMinutes is not null) body["timeoutMinutes"] = timeoutMinutes;
        if (outputDriveItemId is not null) body["outputDriveItemId"] = outputDriveItemId;
        if (clearOutputDriveItemId) body["clearOutputDriveItemId"] = true;

        var req = new HttpRequestMessage(HttpMethod.Patch, $"api/workflows/{workflowId}/steps/{stepId}")
        {
            Content = JsonContent.Create(body)
        };
        var resp = await _http.SendAsync(req);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> RemoveWorkflowStepAsync(string workflowId, string stepId)
    {
        var resp = await _http.DeleteAsync($"api/workflows/{workflowId}/steps/{stepId}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> ReorderWorkflowStepsAsync(string workflowId, IEnumerable<string> orderedStepIds)
    {
        var body = new { orderedStepIds = orderedStepIds.ToArray() };
        var resp = await _http.PostAsJsonAsync($"api/workflows/{workflowId}/steps/reorder", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> RunWorkflowAsync(string workflowId, string agentId, string provider)
    {
        var body = new { agentId, provider };
        var resp = await _http.PostAsJsonAsync($"api/workflows/{workflowId}/run", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetWorkflowExecutionAsync(string executionId)
    {
        var resp = await _http.GetAsync($"api/workflow-executions/{executionId}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Dictionary<string, object?> SerializeInput(StepInputArg input) => input.Kind switch
    {
        "drive" => new() { ["type"] = "drive_document", ["driveItemId"] = input.Id },
        "step" => new() { ["type"] = "previous_step_output", ["stepId"] = input.Id },
        _ => throw new InvalidOperationException($"Unknown input kind: {input.Kind}")
    };

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
