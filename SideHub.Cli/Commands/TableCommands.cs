using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SideHub.Cli.Commands;

public static class TableCommands
{
    public static async Task<int> ShowAsync(SideHubApiClient client, string[] args, bool json)
    {
        var pageId = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(pageId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli table show <pageId>");
            return 1;
        }

        var item = await client.GetDriveItemAsync(pageId);
        if (!EnsureSpreadsheet(item, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var schema = ParseSchema(item.GetProperty("content").GetString() ?? "");

        if (json)
        {
            Console.WriteLine(schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        PrintTable(schema, item);
        return 0;
    }

    public static async Task<int> AppendRowAsync(SideHubApiClient client, string[] args, bool json)
    {
        var pageId = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(pageId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli table append-row <pageId> --col \"Name=Value\" [--col ...]");
            return 1;
        }

        var item = await client.GetDriveItemAsync(pageId);
        if (!EnsureSpreadsheet(item, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var schema = ParseSchema(item.GetProperty("content").GetString() ?? "");
        var columns = schema["columns"]!.AsArray();
        if (columns.Count == 0)
        {
            Console.Error.WriteLine("Error: spreadsheet has no columns. Add one with `table add-column`.");
            return 1;
        }

        var pairs = ParseColPairs(args);
        if (pairs.Count == 0)
        {
            Console.Error.WriteLine("Error: provide at least one --col \"Name=Value\".");
            return 1;
        }

        var cells = new JsonObject();
        foreach (var (name, value) in pairs)
        {
            var col = FindColumnByName(columns, name);
            if (col is null)
            {
                Console.Error.WriteLine($"Error: unknown column '{name}'. Existing columns: {string.Join(", ", columns.Select(c => c!["name"]!.GetValue<string>()))}");
                return 1;
            }
            cells[col["id"]!.GetValue<string>()] = value;
        }

        var newRow = new JsonObject
        {
            ["id"] = NewId("row"),
            ["cells"] = cells,
        };
        schema["rows"]!.AsArray().Add(newRow);

        await SaveAsync(client, pageId, schema);

        if (json) Console.WriteLine(newRow.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        else Console.WriteLine($"Row added: {newRow["id"]!.GetValue<string>()}");
        return 0;
    }

    public static async Task<int> SetCellAsync(SideHubApiClient client, string[] args, bool json)
    {
        var pageId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var rowId = GetOption(args, "--row");
        var colName = GetOption(args, "--col");
        var value = GetOption(args, "--value");

        if (string.IsNullOrEmpty(pageId) || string.IsNullOrEmpty(rowId) || string.IsNullOrEmpty(colName) || value is null)
        {
            Console.Error.WriteLine("Usage: sidehub-cli table set-cell <pageId> --row <rowId> --col <colName> --value \"...\"");
            return 1;
        }

        var item = await client.GetDriveItemAsync(pageId);
        if (!EnsureSpreadsheet(item, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var schema = ParseSchema(item.GetProperty("content").GetString() ?? "");
        var col = FindColumnByName(schema["columns"]!.AsArray(), colName);
        if (col is null) { Console.Error.WriteLine($"Error: unknown column '{colName}'."); return 1; }

        if (col["type"]?.GetValue<string>() == "dropdown" && !string.IsNullOrEmpty(value))
        {
            var allowed = GetDropdownValues(col);
            if (!allowed.Contains(value))
            {
                Console.Error.WriteLine($"Error: value '{value}' is not in the dropdown options of '{colName}'. Allowed: {string.Join(", ", allowed)}.");
                return 1;
            }
        }

        var row = FindRowById(schema["rows"]!.AsArray(), rowId);
        if (row is null) { Console.Error.WriteLine($"Error: unknown row '{rowId}'."); return 1; }

        var cells = row["cells"]!.AsObject();
        cells[col["id"]!.GetValue<string>()] = value;

        await SaveAsync(client, pageId, schema);

        if (json) Console.WriteLine(row.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        else Console.WriteLine("Cell updated.");
        return 0;
    }

    public static async Task<int> DeleteRowAsync(SideHubApiClient client, string[] args, bool json)
    {
        var pageId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var rowId = GetOption(args, "--row");

        if (string.IsNullOrEmpty(pageId) || string.IsNullOrEmpty(rowId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli table delete-row <pageId> --row <rowId>");
            return 1;
        }

        var item = await client.GetDriveItemAsync(pageId);
        if (!EnsureSpreadsheet(item, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var schema = ParseSchema(item.GetProperty("content").GetString() ?? "");
        var rows = schema["rows"]!.AsArray();
        int idx = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i]!["id"]!.GetValue<string>() == rowId) { idx = i; break; }
        }
        if (idx < 0) { Console.Error.WriteLine($"Error: unknown row '{rowId}'."); return 1; }

        rows.RemoveAt(idx);
        await SaveAsync(client, pageId, schema);

        if (json) Console.WriteLine($"{{\"deleted\":\"{rowId}\"}}");
        else Console.WriteLine($"Row deleted: {rowId}");
        return 0;
    }

    public static async Task<int> AddColumnAsync(SideHubApiClient client, string[] args, bool json)
    {
        var pageId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var name = GetOption(args, "--name");
        var type = GetOption(args, "--type") ?? "text";
        var optionsRaw = GetOption(args, "--options");

        if (string.IsNullOrEmpty(pageId) || string.IsNullOrEmpty(name))
        {
            Console.Error.WriteLine("Usage: sidehub-cli table add-column <pageId> --name \"...\" [--type text|image|dropdown] [--options \"val=Label,val2=Label 2\"]");
            return 1;
        }

        if (type != "text" && type != "image" && type != "dropdown")
        {
            Console.Error.WriteLine("Error: --type must be 'text', 'image', or 'dropdown'");
            return 1;
        }

        JsonArray? dropdownOptions = null;
        if (type == "dropdown")
        {
            if (string.IsNullOrWhiteSpace(optionsRaw))
            {
                Console.Error.WriteLine("Error: --options is required when --type dropdown. Format: \"value=Label,value2=Label 2\"");
                return 1;
            }
            if (!TryParseDropdownOptions(optionsRaw!, out dropdownOptions, out var parseError))
            {
                Console.Error.WriteLine($"Error: {parseError}");
                return 1;
            }
        }
        else if (!string.IsNullOrWhiteSpace(optionsRaw))
        {
            Console.Error.WriteLine("Error: --options is only allowed with --type dropdown.");
            return 1;
        }

        var item = await client.GetDriveItemAsync(pageId);
        if (!EnsureSpreadsheet(item, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var schema = ParseSchema(item.GetProperty("content").GetString() ?? "");
        var columns = schema["columns"]!.AsArray();
        if (FindColumnByName(columns, name) is not null)
        {
            Console.Error.WriteLine($"Error: a column named '{name}' already exists.");
            return 1;
        }

        var newCol = new JsonObject
        {
            ["id"] = NewId("col"),
            ["name"] = name,
            ["type"] = type,
            ["width"] = type switch { "image" => 90, "dropdown" => 140, _ => 160 },
        };
        if (dropdownOptions is not null) newCol["options"] = dropdownOptions;
        columns.Add(newCol);

        await SaveAsync(client, pageId, schema);

        if (json) Console.WriteLine(newCol.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        else Console.WriteLine($"Column added: {newCol["id"]!.GetValue<string>()} (\"{name}\", {type})");
        return 0;
    }

    // --- helpers ---

    private static bool EnsureSpreadsheet(JsonElement item, out string error)
    {
        var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type != "spreadsheet")
        {
            error = $"Error: drive item is not a spreadsheet (type={type ?? "?"}).";
            return false;
        }
        if (!item.TryGetProperty("content", out _))
        {
            error = "Error: spreadsheet has no content payload.";
            return false;
        }
        error = "";
        return true;
    }

    private static JsonObject ParseSchema(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return EmptySchema();

        try
        {
            var node = JsonNode.Parse(raw)?.AsObject();
            if (node is null) return EmptySchema();
            node["version"] ??= 1;
            node["columns"] ??= new JsonArray();
            node["rows"] ??= new JsonArray();
            return node;
        }
        catch (JsonException)
        {
            return EmptySchema();
        }
    }

    private static JsonObject EmptySchema() => new()
    {
        ["version"] = 1,
        ["columns"] = new JsonArray(),
        ["rows"] = new JsonArray(),
    };

    private static async Task SaveAsync(SideHubApiClient client, string pageId, JsonObject schema)
    {
        var content = schema.ToJsonString();
        if (content.Length > 100 * 1024)
            throw new InvalidOperationException("Spreadsheet content exceeds 100KB limit.");
        await client.UpdateDriveItemAsync(pageId, null, content);
    }

    private static JsonObject? FindColumnByName(JsonArray columns, string name) =>
        columns.OfType<JsonObject>().FirstOrDefault(c =>
            string.Equals(c["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));

    private static JsonObject? FindRowById(JsonArray rows, string id) =>
        rows.OfType<JsonObject>().FirstOrDefault(r => r["id"]?.GetValue<string>() == id);

    private static List<(string Name, string Value)> ParseColPairs(string[] args)
    {
        var pairs = new List<(string, string)>();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--col") continue;
            var raw = args[i + 1];
            var eq = raw.IndexOf('=');
            if (eq <= 0) continue;
            pairs.Add((raw[..eq].Trim(), raw[(eq + 1)..]));
        }
        return pairs;
    }

    private static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    private static bool TryParseDropdownOptions(string raw, out JsonArray options, out string error)
    {
        options = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tokens = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim();
            if (token.Length == 0) continue;
            var eq = token.IndexOf('=');
            string value, label;
            if (eq < 0)
            {
                value = token;
                label = token;
            }
            else
            {
                value = token[..eq].Trim();
                label = token[(eq + 1)..].Trim();
            }
            if (string.IsNullOrEmpty(value)) { error = $"option '{token}' has an empty value."; return false; }
            if (string.IsNullOrEmpty(label)) label = value;
            if (!seen.Add(value)) { error = $"duplicate option value '{value}'."; return false; }
            options.Add(new JsonObject { ["value"] = value, ["label"] = label });
        }
        if (options.Count == 0) { error = "no valid options parsed."; return false; }
        error = "";
        return true;
    }

    private static HashSet<string> GetDropdownValues(JsonObject column)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (column["options"] is not JsonArray options) return set;
        foreach (var opt in options.OfType<JsonObject>())
        {
            var v = opt["value"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(v)) set.Add(v);
        }
        return set;
    }

    private static string? FindDropdownLabel(JsonObject column, string value)
    {
        if (column["options"] is not JsonArray options) return null;
        foreach (var opt in options.OfType<JsonObject>())
        {
            if (opt["value"]?.GetValue<string>() == value)
                return opt["label"]?.GetValue<string>();
        }
        return null;
    }

    private static string NewId(string prefix)
    {
        var bytes = Guid.NewGuid().ToByteArray();
        var hex = Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
        return $"{prefix}_{hex}";
    }

    private static void PrintTable(JsonObject schema, JsonElement item)
    {
        var columns = schema["columns"]!.AsArray().OfType<JsonObject>().ToList();
        var rows = schema["rows"]!.AsArray().OfType<JsonObject>().ToList();

        if (columns.Count == 0)
        {
            Console.WriteLine("(empty spreadsheet — no columns yet)");
            return;
        }

        Dictionary<string, string>? imageNames = null;
        if (item.TryGetProperty("referencedImages", out var refs) && refs.ValueKind == JsonValueKind.Object)
        {
            imageNames = new Dictionary<string, string>();
            foreach (var prop in refs.EnumerateObject())
            {
                if (prop.Value.TryGetProperty("fileName", out var fn) && fn.ValueKind == JsonValueKind.String)
                    imageNames[prop.Name] = fn.GetString() ?? "";
            }
        }

        var headers = columns.Select(c => c["name"]?.GetValue<string>() ?? "?").ToArray();
        var widths = headers.Select(h => h.Length).ToArray();

        var data = new List<string[]>();
        foreach (var row in rows)
        {
            var cells = row["cells"] as JsonObject;
            var values = new string[columns.Count];
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                var colId = col["id"]?.GetValue<string>() ?? "";
                var raw = cells?[colId]?.GetValue<string>() ?? "";
                var colType = col["type"]?.GetValue<string>();
                if (colType == "image" && !string.IsNullOrEmpty(raw))
                {
                    var name = imageNames?.TryGetValue(raw, out var n) == true ? n : "(missing)";
                    raw = $"[img: {name}]";
                }
                else if (colType == "dropdown" && !string.IsNullOrEmpty(raw))
                {
                    var label = FindDropdownLabel(col, raw);
                    if (label is not null && label != raw) raw = $"{label} ({raw})";
                }
                values[i] = raw;
                if (raw.Length > widths[i]) widths[i] = raw.Length;
            }
            data.Add(values);
        }

        // cap width
        for (int i = 0; i < widths.Length; i++) widths[i] = Math.Min(widths[i], 40);

        var idColWidth = Math.Max(7, rows.Select(r => r["id"]!.GetValue<string>().Length).DefaultIfEmpty(7).Max());

        var sb = new StringBuilder();
        sb.Append("ROW".PadRight(idColWidth)).Append(" │ ");
        for (int i = 0; i < headers.Length; i++)
        {
            sb.Append(Pad(headers[i], widths[i]));
            if (i < headers.Length - 1) sb.Append(" │ ");
        }
        Console.WriteLine(sb.ToString());
        Console.WriteLine(new string('─', sb.Length));

        foreach (var (row, values) in rows.Zip(data))
        {
            var line = new StringBuilder();
            line.Append(row["id"]!.GetValue<string>().PadRight(idColWidth)).Append(" │ ");
            for (int i = 0; i < values.Length; i++)
            {
                line.Append(Pad(values[i], widths[i]));
                if (i < values.Length - 1) line.Append(" │ ");
            }
            Console.WriteLine(line.ToString());
        }

        Console.WriteLine();
        Console.WriteLine($"{rows.Count} row(s), {columns.Count} column(s)");
    }

    private static string Pad(string s, int width)
    {
        if (s.Length > width) return s[..(width - 1)] + "…";
        return s.PadRight(width);
    }
}
