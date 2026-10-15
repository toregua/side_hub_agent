namespace SideHub.Agent.Tests;

/// <summary>The reaper stops a terminal left alone, never one whose CLI is still working or whose queued task is open.</summary>
public class IdlePtyPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 2, 8, 0, DateTimeKind.Utc);

    [Fact]
    public void A_terminal_without_input_or_output_for_30_minutes_is_stopped()
    {
        Assert.True(IdlePtyPolicy.ShouldReap(Now, Now.AddMinutes(-31), Now.AddMinutes(-40), CliStates.Idle, openReviewRound: false));
        Assert.True(IdlePtyPolicy.ShouldReap(Now, Now.AddMinutes(-31), lastOutput: null, cliState: null, openReviewRound: false));
        Assert.False(IdlePtyPolicy.ShouldReap(Now, Now.AddMinutes(-29), lastOutput: null, cliState: null, openReviewRound: false));
    }

    [Fact]
    public void A_terminal_still_printing_is_kept_without_input()
    {
        Assert.False(IdlePtyPolicy.ShouldReap(Now, Now.AddHours(-2), Now.AddMinutes(-1), cliState: null, openReviewRound: false));
    }

    [Theory]
    [InlineData(CliStates.Working)]
    [InlineData(CliStates.WaitingInput)]
    public void A_busy_cli_is_kept_up_to_4_hours_without_input_or_output(string state)
    {
        Assert.False(IdlePtyPolicy.ShouldReap(Now, Now.AddHours(-3), Now.AddHours(-3), state, openReviewRound: false));
        // Its state can stay "working" after a turn interrupted with Escape: a frozen CLI is stopped in the end.
        Assert.True(IdlePtyPolicy.ShouldReap(Now, Now.AddHours(-5), Now.AddHours(-4.5), state, openReviewRound: false));
    }

    [Fact]
    public void The_terminal_of_an_open_review_round_is_never_stopped()
    {
        Assert.False(IdlePtyPolicy.ShouldReap(Now, Now.AddDays(-2), Now.AddDays(-2), CliStates.Idle, openReviewRound: true));
    }
}
