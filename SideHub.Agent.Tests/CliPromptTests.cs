using System.Text;
using System.Text.Json;
using SideHub.Agent.Models;
using SideHub.Cli.Launch;

namespace SideHub.Agent.Tests;

/// <summary>What a waiting Claude asks, read from its hook (sidehub-cli), carried through the FIFO (agent) and tracked,
/// so the phone can answer with a tap.</summary>
public class CliPromptTests
{
    private const string Session = "8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14";
    private const string Secret = "4f1c0d2e9b8a7c6d5e4f3a2b1c0d9e8f";

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string AskUserQuestion(string questions) =>
        $$$"""{"session_id":"{{{Session}}}","hook_event_name":"PreToolUse","tool_name":"AskUserQuestion","tool_input":{"questions":{{{questions}}}}}""";

    [Fact]
    public void A_single_choice_question_gives_its_question_header_and_options()
    {
        var prompt = CliPrompt.FromHookInput(Utf8(AskUserQuestion("""
            [{"question":"Quelle couleur ?","header":"Couleur","multiSelect":false,
              "options":[{"label":"Rouge","description":"La couleur rouge"},{"label":"Bleu"}]}]
            """)));

        Assert.NotNull(prompt);
        Assert.Equal("question", (string?)prompt["kind"]);
        Assert.Equal("Quelle couleur ?", (string?)prompt["question"]);
        Assert.Equal("Couleur", (string?)prompt["header"]);
        var options = prompt["options"]!.AsArray();
        Assert.Equal(2, options.Count);
        Assert.Equal("Rouge", (string?)options[0]!["label"]);
        Assert.Equal("La couleur rouge", (string?)options[0]!["description"]);
        Assert.Null(options[1]!["description"]);
    }

    [Theory]
    [InlineData("""[{"question":"A ?","options":[{"label":"x"}]},{"question":"B ?","options":[{"label":"y"}]}]""")]
    [InlineData("""[{"question":"A ?","multiSelect":true,"options":[{"label":"x"},{"label":"y"}]}]""")]
    [InlineData("""[{"question":"A ?","options":[{"label":"1"},{"label":"2"},{"label":"3"},{"label":"4"},{"label":"5"}]}]""")]
    [InlineData("""[{"question":"","options":[{"label":"x"}]}]""")]
    [InlineData("""[{"question":"A ?","options":[{"description":"no label"}]}]""")]
    public void Several_questions_multiple_choice_or_odd_shapes_give_no_prompt(string questions) =>
        Assert.Null(CliPrompt.FromHookInput(Utf8(AskUserQuestion(questions))));

    [Fact]
    public void A_permission_gives_the_tool_and_what_it_would_run()
    {
        var prompt = CliPrompt.FromHookInput(Utf8($$$"""
            {"session_id":"{{{Session}}}","hook_event_name":"PermissionRequest","tool_name":"Bash",
             "tool_input":{"command":"git push origin main","description":"Push the commit"}}
            """));

        Assert.NotNull(prompt);
        Assert.Equal("permission", (string?)prompt["kind"]);
        Assert.Equal("Bash", (string?)prompt["tool"]);
        Assert.Equal("git push origin main", (string?)prompt["detail"]);
        Assert.Equal("Push the commit", (string?)prompt["description"]);
    }

    [Fact]
    public void Long_texts_are_cut()
    {
        var prompt = CliPrompt.FromHookInput(Utf8($$$"""
            {"hook_event_name":"PermissionRequest","tool_name":"Write","tool_input":{"file_path":"/{{{new string('a', 2000)}}}"}}
            """));

        Assert.Equal(CliPrompt.MaxDetail, ((string?)prompt!["detail"])!.Length);
        Assert.EndsWith("…", (string?)prompt["detail"]);
    }

    [Fact]
    public void The_permission_claude_asks_for_AskUserQuestion_is_the_question_itself()
    {
        var input = AskUserQuestion("""[{"question":"A ?","options":[{"label":"x"},{"label":"y"}]}]""")
            .Replace("\"PreToolUse\"", "\"PermissionRequest\"");

        var prompt = CliPrompt.FromHookInput(Utf8(input));

        Assert.Equal("question", (string?)prompt!["kind"]);
        Assert.Equal("A ?", (string?)prompt["question"]);
    }

    [Theory]
    [InlineData("""{"hook_event_name":"Notification","notification_type":"permission_prompt","message":"Claude needs your permission"}""")]
    [InlineData("""{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"ls"}}""")]
    [InlineData("""{"hook_event_name":"PermissionRequest","tool_input":{"content":"cut short""")]
    [InlineData("not json")]
    public void Other_hooks_and_cut_inputs_give_no_prompt(string input) =>
        Assert.Null(CliPrompt.FromHookInput(Utf8(input)));

    [Fact]
    public void The_claude_waiting_input_report_carries_the_prompt_and_other_states_never_do()
    {
        var input = Utf8(AskUserQuestion("""[{"question":"A ?","options":[{"label":"x"},{"label":"y"}]}]"""));

        var waiting = CliStateCommand.Report(["claude", "waiting-input"], () => input)!;
        Assert.Equal(Session, waiting.CliSessionId);
        Assert.Equal("question", (string?)waiting.Prompt!["kind"]);
        Assert.Null(CliStateCommand.Report(["claude", "working"], () => input)!.Prompt);
    }

    // === Agent side: the FIFO line and the tracker ===

    private static FifoNotification.CliStateChanged? ParseState(string prompt) =>
        FifoNotification.Parse(
            $$"""{"secret":"{{Secret}}","event":"cli-state","provider":"claude","state":"waiting-input","cliSessionId":"{{Session}}","prompt":{{prompt}}}""",
            Secret, out _) as FifoNotification.CliStateChanged;

    [Fact]
    public void The_fifo_line_keeps_a_well_formed_prompt()
    {
        var changed = ParseState("""{"kind":"question","question":"A ?","header":"H","options":[{"label":"x","description":"d"},{"label":"y"}]}""");

        Assert.NotNull(changed);
        Assert.Equal(CliStates.WaitingInput, changed.State);
        Assert.Equal("A ?", changed.Prompt!.Question);
        Assert.Equal(["x", "y"], changed.Prompt.Options!.Select(o => o.Label));
    }

    [Theory]
    [InlineData("""{"kind":"question","question":"A ?","options":[]}""")]
    [InlineData("""{"kind":"question","question":"A ?","options":[{"label":"x"},{"label":"x"},{"label":"x"},{"label":"x"},{"label":"x"}]}""")]
    [InlineData("""{"kind":"question","question":"A\u0007 ?","options":[{"label":"x"}]}""")]
    [InlineData("""{"kind":"permission"}""")]
    [InlineData("""{"kind":"shell","tool":"Bash"}""")]
    [InlineData("\"text\"")]
    public void A_malformed_prompt_is_dropped_and_the_state_kept(string prompt)
    {
        var changed = ParseState(prompt);

        Assert.NotNull(changed);
        Assert.Equal(CliStates.WaitingInput, changed.State);
        Assert.Null(changed.Prompt);
    }

    [Fact]
    public void A_too_long_prompt_text_is_dropped()
    {
        var question = new string('q', PtyCliPrompt.MaxQuestion + 1);
        Assert.Null(ParseState($$"""{"kind":"question","question":"{{question}}","options":[{"label":"x"}]}""")!.Prompt);
    }

    [Fact]
    public void The_tracker_sends_a_prompt_that_follows_the_same_state_and_keeps_it_on_a_bare_repeat()
    {
        var tracker = new CliStateTracker();
        var t0 = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var permission = new PtyCliPrompt(PtyCliPrompt.PermissionKind, Tool: "Bash", Detail: "touch /tmp/x");

        // Claude's Notification hook (no prompt), then its PermissionRequest hook (with the prompt)
        Assert.NotNull(tracker.Record("pty", "claude", CliStates.WaitingInput, Session, t0));
        var withPrompt = tracker.Record("pty", "claude", CliStates.WaitingInput, Session, t0, permission);
        Assert.Equal("touch /tmp/x", withPrompt!.Prompt!.Detail);

        // The other order: the bare repeat neither resends nor loses the prompt
        Assert.Null(tracker.Record("pty", "claude", CliStates.WaitingInput, Session, t0));
        Assert.Null(tracker.Record("pty", "claude", CliStates.WaitingInput, Session, t0,
            new PtyCliPrompt(PtyCliPrompt.PermissionKind, Tool: "Bash", Detail: "touch /tmp/x")));
        Assert.Equal("touch /tmp/x", tracker.Current("pty")!.Prompt!.Detail);

        // Answered: working again, the prompt is gone
        Assert.Null(tracker.Record("pty", "claude", CliStates.Working, Session, t0, permission)!.Prompt);
    }

    [Fact]
    public void The_message_to_the_backend_has_the_prompt_in_camel_case_without_empty_fields()
    {
        var message = new PtyCliStateMessage
        {
            PtySessionId = "pty", Provider = "claude", State = CliStates.WaitingInput, At = DateTime.UnixEpoch,
            Prompt = new PtyCliPrompt(PtyCliPrompt.QuestionKind, Question: "A ?", Options: [new PtyCliPromptOption("x")]),
        };

        var json = JsonSerializer.Serialize(message);

        Assert.Contains("\"prompt\":{\"kind\":\"question\",\"question\":\"A ?\",\"options\":[{\"label\":\"x\"}]}", json);
    }
}
