using System.Globalization;
using System.Text.Json;

namespace SideHub.Cli.Commands;

/// <summary>
/// The workspace's repositories, and questions asked about their code: each question is a read-only run of the
/// repository's agent, in its own checkout of the default branch, answered in a few minutes.
/// </summary>
public static class RepositoryCommands
{
    /// <summary>Seconds between two reads of a pending question.</summary>
    internal const int PollSeconds = 5;
    /// <summary>
    /// Minutes <c>repository ask</c> waits by default: the question's run times out at 15, and a run that succeeds gets 2
    /// more for its answer to arrive.
    /// </summary>
    private const int DefaultTimeoutMinutes = 17;
    private const int QuestionColumn = 60;

    public static async Task<int> ListAsync(SideHubApiClient client, bool json)
    {
        var result = await client.GetRepositoriesAsync();

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        if (!result.TryGetProperty("repositories", out var repositories) || repositories.GetArrayLength() == 0)
        {
            Console.WriteLine("No repositories found.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"BRANCH",-14} {"NAME"}");
        Console.WriteLine(new string('-', 80));
        foreach (var repository in repositories.EnumerateArray())
        {
            var id = repository.GetProperty("id").GetString() ?? "";
            var branch = repository.TryGetProperty("defaultBranch", out var b) ? b.GetString() ?? "" : "";
            var name = repository.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            Console.WriteLine($"{id,-38} {branch,-14} {name}");
        }
        return 0;
    }

    public static Task<int> AskAsync(SideHubApiClient client, string[] args, bool json) =>
        AskAsync(client, args, json, () => Task.Delay(TimeSpan.FromSeconds(PollSeconds)));

    /// <param name="wait">Waits between two reads of the pending question (<see cref="PollSeconds"/>).</param>
    internal static async Task<int> AskAsync(SideHubApiClient client, string[] args, bool json, Func<Task> wait)
    {
        var positionals = Positionals(args, "--timeout");
        var timeoutArg = GetOption(args, "--timeout");
        var timeoutMinutes = DefaultTimeoutMinutes;
        var question = string.Join(" ", positionals.Skip(1)).Trim();
        if (positionals.Count < 2 || question.Length == 0
            || (timeoutArg is not null && (!int.TryParse(timeoutArg, out timeoutMinutes) || timeoutMinutes < 1)))
        {
            Console.Error.WriteLine("Usage: sidehub-cli repository ask <repository id|name> \"question\" [--no-wait] [--timeout <minutes>]");
            return 1;
        }

        var asked = await client.AskRepositoryQuestionAsync(positionals[0], question);
        var pending = asked.GetProperty("question");
        var questionId = Prop(pending, "id");
        var agentName = Prop(asked, "agentName") ?? "?";

        if (args.Contains("--no-wait"))
        {
            if (json)
            {
                Console.WriteLine(SideHubApiClient.Serialize(asked));
                return 0;
            }
            Console.WriteLine($"Asked {positionals[0]} (agent {agentName}): question {questionId}.");
            Console.WriteLine($"Read the answer in a few minutes: sidehub-cli repository question {questionId}");
            return 0;
        }

        Console.Error.WriteLine($"Asked {positionals[0]} (agent {agentName}), waiting for the answer...");
        var current = pending;
        var polls = timeoutMinutes * 60 / PollSeconds;
        for (var poll = 0; IsPending(current) && poll < polls; poll++)
        {
            await wait();
            current = await client.GetRepositoryQuestionAsync(questionId!);
        }

        if (IsPending(current))
        {
            if (json)
                Console.WriteLine(SideHubApiClient.Serialize(current));
            Console.Error.WriteLine($"No answer after {timeoutMinutes} min. Read it later: sidehub-cli repository question {questionId}");
            return 1;
        }

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(current));
            return Prop(current, "status") == "answered" ? 0 : 1;
        }

        if (Prop(current, "status") != "answered")
        {
            Console.Error.WriteLine($"Question {questionId} {Prop(current, "status")}: {Prop(current, "error") ?? "no reason given."}");
            return 1;
        }

        Console.WriteLine(Prop(current, "answer"));
        Console.WriteLine();
        Console.WriteLine(Commit(current));
        return 0;
    }

    public static async Task<int> QuestionAsync(SideHubApiClient client, string[] args, bool json)
    {
        var questionId = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(questionId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli repository question <questionId>");
            return 1;
        }

        var question = await client.GetRepositoryQuestionAsync(questionId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(question));
            return 0;
        }

        Console.WriteLine($"Question:   {Prop(question, "id")}");
        Console.WriteLine($"Status:     {Prop(question, "status")}");
        Console.WriteLine($"Repository: {Prop(question, "repositoryId")}");
        Console.WriteLine($"Run:        {Prop(question, "runId")}");
        Console.WriteLine($"Asked:      {PropDate(question, "askedAt")}");
        Console.WriteLine($"Settled:    {PropDate(question, "settledAt")}");
        if (Prop(question, "commitSha") is not null)
            Console.WriteLine($"Commit:     {Prop(question, "commitSha")} ({PropDate(question, "commitDate")})");
        Console.WriteLine();
        Console.WriteLine(Prop(question, "question"));
        if (Prop(question, "answer") is { } answer)
        {
            Console.WriteLine();
            Console.WriteLine("Answer:");
            Console.WriteLine(answer);
        }
        if (Prop(question, "error") is { } error)
        {
            Console.WriteLine();
            Console.WriteLine($"Error: {error}");
        }
        return 0;
    }

    public static async Task<int> QuestionsAsync(SideHubApiClient client, string[] args, bool json)
    {
        var limitArg = GetOption(args, "--limit");
        int? limit = null;
        if (limitArg is not null)
        {
            if (!int.TryParse(limitArg, out var parsed) || parsed < 1)
            {
                Console.Error.WriteLine("Usage: sidehub-cli repository questions [--limit N]");
                return 1;
            }
            limit = parsed;
        }

        var questions = await client.GetRepositoryQuestionsAsync(limit);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(questions));
            return 0;
        }

        if (questions.ValueKind != JsonValueKind.Array || questions.GetArrayLength() == 0)
        {
            Console.WriteLine("No questions asked in this workspace.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"STATUS",-9} {"REPOSITORY",-38} {"ASKED",-22} {"QUESTION"}");
        Console.WriteLine(new string('-', 160));
        foreach (var q in questions.EnumerateArray())
        {
            var text = (Prop(q, "question") ?? "").ReplaceLineEndings(" ").Trim();
            if (text.Length > QuestionColumn)
                text = text[..(QuestionColumn - 3)] + "...";
            Console.WriteLine($"{Prop(q, "id"),-38} {Prop(q, "status"),-9} {Prop(q, "repositoryId"),-38} {PropDate(q, "askedAt"),-22} {text}");
        }
        return 0;
    }

    private static bool IsPending(JsonElement question) => Prop(question, "status") == "pending";

    private static string Commit(JsonElement question) =>
        Prop(question, "commitSha") is { } sha
            ? $"Read at commit {sha} ({PropDate(question, "commitDate")})."
            : "Read at an unknown commit.";

    /// <summary>The arguments that are neither a flag nor the value of one of <paramref name="valueFlags"/>.</summary>
    private static List<string> Positionals(string[] args, params string[] valueFlags)
    {
        var result = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (valueFlags.Contains(args[i])) { i++; continue; }
            if (!args[i].StartsWith("--")) result.Add(args[i]);
        }
        return result;
    }

    private static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return null;
    }

    private static string? Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;

    private static string PropDate(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToString("yyyy-MM-dd HH:mm UTC") : "-";
}
