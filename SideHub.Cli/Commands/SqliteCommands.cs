using System.Text.Json;

namespace SideHub.Cli.Commands;

public static class SqliteCommands
{
    private static readonly HashSet<string> QueryFlags = new()
    {
        "--sql", "--param", "--row-limit", "--timeout", "--json"
    };
    private static readonly HashSet<string> ExecFlags = new()
    {
        "--sql", "--param", "--allow-ddl", "--timeout", "--json"
    };
    private static readonly HashSet<string> SchemaFlags = new() { "--json" };
    private static readonly HashSet<string> CreateFlags = new()
    {
        "--title", "--schema", "--schema-file", "--parent", "--json"
    };

    public static async Task<int> QueryAsync(SideHubApiClient client, string[] args, bool json)
    {
        if (ValidateKnownFlags(args, QueryFlags) is { } err) return err;

        var itemId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var sql = GetOption(args, "--sql");
        if (string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(sql))
        {
            Console.Error.WriteLine("Usage: sidehub-cli sqlite query <itemId> --sql \"SELECT ...\" [--param V]* [--row-limit N] [--timeout SEC] [--json]");
            return 1;
        }

        var parameters = GetAllOptions(args, "--param").Cast<object?>().ToArray();
        var rowLimit = ParseIntOption(args, "--row-limit");
        var timeout = ParseIntOption(args, "--timeout");

        var result = await client.SqliteQueryAsync(itemId, sql, parameters, rowLimit, timeout);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        PrintTable(result);
        return 0;
    }

    public static async Task<int> ExecAsync(SideHubApiClient client, string[] args, bool json)
    {
        if (ValidateKnownFlags(args, ExecFlags) is { } err) return err;

        var itemId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var sql = GetOption(args, "--sql");
        if (string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(sql))
        {
            Console.Error.WriteLine("Usage: sidehub-cli sqlite exec <itemId> --sql \"INSERT ...\" [--param V]* [--allow-ddl] [--timeout SEC] [--json]");
            return 1;
        }

        var parameters = GetAllOptions(args, "--param").Cast<object?>().ToArray();
        var allowDdl = args.Contains("--allow-ddl");
        var timeout = ParseIntOption(args, "--timeout");

        var result = await client.SqliteExecAsync(itemId, sql, parameters, allowDdl, timeout);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        var rows = result.TryGetProperty("rowsAffected", out var ra) ? ra.GetInt32() : 0;
        var size = result.TryGetProperty("newFileSize", out var sz) ? sz.GetInt64() : 0;
        var ms = result.TryGetProperty("durationMs", out var dm) ? dm.GetInt64() : 0;
        Console.WriteLine($"OK — rows affected: {rows}, db size: {size} bytes, duration: {ms} ms");
        return 0;
    }

    public static async Task<int> SchemaAsync(SideHubApiClient client, string[] args, bool json)
    {
        if (ValidateKnownFlags(args, SchemaFlags) is { } err) return err;

        var itemId = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(itemId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli sqlite schema <itemId> [--json]");
            return 1;
        }

        var result = await client.SqliteSchemaAsync(itemId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        if (!result.TryGetProperty("objects", out var objects) || objects.GetArrayLength() == 0)
        {
            Console.WriteLine("(empty database)");
            return 0;
        }

        foreach (var obj in objects.EnumerateArray())
        {
            var name = obj.GetProperty("name").GetString() ?? "?";
            var type = obj.GetProperty("type").GetString() ?? "?";
            Console.WriteLine($"\n[{type}] {name}");
            if (obj.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cols.EnumerateArray())
                {
                    var cn = c.GetProperty("name").GetString();
                    var ct = c.GetProperty("type").GetString();
                    var pk = c.GetProperty("primaryKey").GetBoolean() ? " PK" : "";
                    var nn = c.GetProperty("notNull").GetBoolean() ? " NOT NULL" : "";
                    Console.WriteLine($"  {cn} {ct}{pk}{nn}");
                }
            }
            if (obj.TryGetProperty("sql", out var sql) && sql.ValueKind == JsonValueKind.String)
            {
                Console.WriteLine($"  -- {sql.GetString()?.Replace("\n", "\n  -- ")}");
            }
        }
        return 0;
    }

    public static async Task<int> CreateAsync(SideHubApiClient client, string[] args, bool json)
    {
        if (ValidateKnownFlags(args, CreateFlags) is { } err) return err;

        var title = GetOption(args, "--title");
        var schema = GetOption(args, "--schema");
        var schemaFile = GetOption(args, "--schema-file");
        var parentId = GetOption(args, "--parent");

        if (string.IsNullOrEmpty(title))
        {
            Console.Error.WriteLine("Usage: sidehub-cli sqlite create --title \"name\" [--schema \"CREATE TABLE ...\" | --schema-file <path>] [--parent <id>] [--json]");
            return 1;
        }

        if (!string.IsNullOrEmpty(schemaFile))
        {
            if (!File.Exists(schemaFile))
            {
                Console.Error.WriteLine($"Error: schema file not found: {schemaFile}");
                return 1;
            }
            schema = await File.ReadAllTextAsync(schemaFile);
        }

        var result = await client.CreateSqliteDatabaseAsync(title, schema, parentId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        var id = result.TryGetProperty("id", out var i) ? i.GetString() : null;
        Console.WriteLine($"Created SQLite database: {id}");
        return 0;
    }

    private static void PrintTable(JsonElement result)
    {
        if (!result.TryGetProperty("columns", out var cols) || !result.TryGetProperty("rows", out var rows))
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return;
        }

        var columns = cols.EnumerateArray().Select(c => c.GetString() ?? "").ToArray();
        var rowList = rows.EnumerateArray().ToArray();

        if (rowList.Length == 0)
        {
            Console.WriteLine("(no rows)");
            return;
        }

        var widths = new int[columns.Length];
        for (int i = 0; i < columns.Length; i++) widths[i] = columns[i].Length;
        foreach (var row in rowList)
        {
            int j = 0;
            foreach (var cell in row.EnumerateArray())
            {
                var s = cell.ValueKind == JsonValueKind.Null ? "NULL" : cell.ToString();
                if (s.Length > widths[j]) widths[j] = Math.Min(s.Length, 40);
                j++;
            }
        }

        Console.WriteLine(string.Join(" | ", columns.Select((c, i) => c.PadRight(widths[i]))));
        Console.WriteLine(string.Join("-+-", widths.Select(w => new string('-', w))));
        foreach (var row in rowList)
        {
            var cells = row.EnumerateArray().Select((c, i) =>
            {
                var s = c.ValueKind == JsonValueKind.Null ? "NULL" : c.ToString();
                if (s.Length > 40) s = s[..37] + "...";
                return s.PadRight(widths[i]);
            });
            Console.WriteLine(string.Join(" | ", cells));
        }

        if (result.TryGetProperty("truncated", out var t) && t.GetBoolean())
            Console.WriteLine("(results truncated — pass --row-limit to expand or refine the query)");
    }

    private static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    private static IEnumerable<string> GetAllOptions(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) yield return args[i + 1];
    }

    private static int? ParseIntOption(string[] args, string flag)
    {
        var s = GetOption(args, flag);
        if (s is null) return null;
        return int.TryParse(s, out var v) ? v : null;
    }

    private static int? ValidateKnownFlags(string[] args, HashSet<string> known)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) continue;
            if (!known.Contains(a))
            {
                Console.Error.WriteLine($"Error: unknown flag '{a}'");
                return 1;
            }
        }
        return null;
    }
}
