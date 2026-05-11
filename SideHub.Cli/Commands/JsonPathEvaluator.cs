using System.Text.Json.Nodes;

namespace SideHub.Cli.Commands;

/// <summary>
/// Minimal JSONPath evaluator supporting the common subset:
///   $              -> root
///   .field         -> property
///   ['field']      -> property (quoted)
///   [N]            -> array index
///   [*]            -> all array elements
///   ..field        -> recursive descent (single level — looks at all descendants)
/// </summary>
public static class JsonPathEvaluator
{
    public static List<JsonNode?> Evaluate(JsonNode? root, string path)
    {
        if (string.IsNullOrEmpty(path) || path == "$")
            return new List<JsonNode?> { root };

        if (!path.StartsWith("$", StringComparison.Ordinal))
            throw new ArgumentException($"JSONPath must start with '$': '{path}'");

        var current = new List<JsonNode?> { root };
        var i = 1;

        while (i < path.Length)
        {
            var c = path[i];
            if (c == '.')
            {
                if (i + 1 < path.Length && path[i + 1] == '.')
                {
                    // recursive descent
                    i += 2;
                    var name = ReadName(path, ref i);
                    current = RecursiveDescent(current, name);
                }
                else
                {
                    i++;
                    var name = ReadName(path, ref i);
                    if (name == "*")
                        current = ExpandAll(current);
                    else
                        current = StepInto(current, name);
                }
            }
            else if (c == '[')
            {
                i++;
                if (i >= path.Length) throw new ArgumentException("Unterminated '['");

                if (path[i] == '*')
                {
                    if (i + 1 >= path.Length || path[i + 1] != ']')
                        throw new ArgumentException("Expected ']' after '*'");
                    i += 2;
                    current = ExpandAll(current);
                }
                else if (path[i] == '\'' || path[i] == '"')
                {
                    var quote = path[i];
                    i++;
                    var end = path.IndexOf(quote, i);
                    if (end < 0) throw new ArgumentException("Unterminated quoted name");
                    var name = path[i..end];
                    i = end + 1;
                    if (i >= path.Length || path[i] != ']') throw new ArgumentException("Expected ']'");
                    i++;
                    current = StepInto(current, name);
                }
                else
                {
                    var end = path.IndexOf(']', i);
                    if (end < 0) throw new ArgumentException("Unterminated '['");
                    var token = path[i..end];
                    i = end + 1;
                    if (int.TryParse(token, out var idx))
                        current = StepIndex(current, idx);
                    else
                        current = StepInto(current, token);
                }
            }
            else
            {
                throw new ArgumentException($"Unexpected character '{c}' at position {i}");
            }
        }
        return current;
    }

    private static string ReadName(string path, ref int i)
    {
        var start = i;
        while (i < path.Length && path[i] != '.' && path[i] != '[')
            i++;
        return path[start..i];
    }

    private static List<JsonNode?> StepInto(List<JsonNode?> nodes, string name)
    {
        var result = new List<JsonNode?>();
        foreach (var node in nodes)
        {
            if (node is JsonObject obj && obj.TryGetPropertyValue(name, out var child))
                result.Add(child);
        }
        return result;
    }

    private static List<JsonNode?> StepIndex(List<JsonNode?> nodes, int idx)
    {
        var result = new List<JsonNode?>();
        foreach (var node in nodes)
        {
            if (node is JsonArray arr)
            {
                var resolved = idx < 0 ? arr.Count + idx : idx;
                if (resolved >= 0 && resolved < arr.Count)
                    result.Add(arr[resolved]);
            }
        }
        return result;
    }

    private static List<JsonNode?> ExpandAll(List<JsonNode?> nodes)
    {
        var result = new List<JsonNode?>();
        foreach (var node in nodes)
        {
            switch (node)
            {
                case JsonArray arr:
                    foreach (var c in arr) result.Add(c);
                    break;
                case JsonObject obj:
                    foreach (var kv in obj) result.Add(kv.Value);
                    break;
            }
        }
        return result;
    }

    private static List<JsonNode?> RecursiveDescent(List<JsonNode?> nodes, string name)
    {
        var result = new List<JsonNode?>();
        foreach (var node in nodes)
            Walk(node, name, result);
        return result;
    }

    private static void Walk(JsonNode? node, string name, List<JsonNode?> sink)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.TryGetPropertyValue(name, out var child)) sink.Add(child);
                foreach (var kv in obj) Walk(kv.Value, name, sink);
                break;
            case JsonArray arr:
                foreach (var c in arr) Walk(c, name, sink);
                break;
        }
    }
}
