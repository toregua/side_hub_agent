using System.Text.Json;
using System.Text.Json.Nodes;
using SideHub.Cli;
using SideHub.Cli.Commands;

namespace SideHub.Agent.Tests;

/// <summary>
/// The 100 KB cap of a spreadsheet page edited with <c>sidehub-cli table</c>: a warning from 80 % so an automation can
/// make room a run before it is blocked, an error that says what to do, and a table already over the cap that can
/// still be shrunk.
/// </summary>
public sealed class CliTableSizeLimitTests : IDisposable
{
    private const string PageId = "tbl-1";
    private const int RowCellLength = 1000;

    private readonly FakeSideHubApi _api = new();
    private readonly SideHubApiClient _client;

    public CliTableSizeLimitTests() =>
        _client = new SideHubApiClient(_api.Url, "sh_pty_test", "11111111-1111-1111-1111-111111111111", "agent-1");

    public void Dispose()
    {
        _client.Dispose();
        _api.Dispose();
    }

    [Fact]
    public async Task A_small_table_is_saved_without_a_warning()
    {
        ServeTable(rows: 10);

        var (code, stderr) = await AppendRowAsync();

        Assert.Equal(0, code);
        Assert.Contains($"PUT /api/drive/{PageId}", _api.Methods);
        Assert.DoesNotContain("Warning", stderr);
    }

    [Fact]
    public async Task A_table_past_80_percent_is_saved_with_a_warning_that_says_what_to_do()
    {
        ServeTable(rows: 85);

        var (code, stderr) = await AppendRowAsync();

        Assert.Equal(0, code);
        Assert.Contains($"PUT /api/drive/{PageId}", _api.Methods);
        Assert.Contains("Warning: spreadsheet \"Daily log\"", stderr);
        Assert.Contains("of the 100 KB limit", stderr);
        Assert.Contains($"table delete-row {PageId}", stderr);
        Assert.Contains("sqlite create", stderr);
    }

    [Fact]
    public async Task A_change_past_the_cap_is_refused_with_the_size_the_rows_and_the_ways_out()
    {
        ServeTable(rows: 101);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AppendRowAsync());

        Assert.DoesNotContain($"PUT /api/drive/{PageId}", _api.Methods);
        Assert.Contains("with 102 row(s), over the 100 KB limit, so nothing was saved", error.Message);
        Assert.Contains($"table delete-row {PageId}", error.Message);
        Assert.Contains("sqlite create", error.Message);
    }

    [Fact]
    public async Task A_table_already_over_the_cap_can_still_be_shrunk()
    {
        ServeTable(rows: 110);

        var stderr = await CaptureStderrAsync(() => TableCommands.DeleteRowAsync(_client, [PageId, "--row", "row_0"], json: false));

        Assert.Equal(0, stderr.Code);
        Assert.Contains($"PUT /api/drive/{PageId}", _api.Methods);
        Assert.Contains("Warning", stderr.Text);
    }

    private Task<(int Code, string Text)> AppendRowAsync() =>
        CaptureStderrAsync(() => TableCommands.AppendRowAsync(_client, [PageId, "--col", $"Note={new string('y', RowCellLength)}"], json: false));

    /// <summary>A spreadsheet of <paramref name="rows"/> rows of about 1 KB each.</summary>
    private void ServeTable(int rows)
    {
        var schema = new JsonObject
        {
            ["version"] = 1,
            ["columns"] = new JsonArray(new JsonObject { ["id"] = "col_a", ["name"] = "Note", ["type"] = "text", ["width"] = 160 }),
            ["rows"] = new JsonArray(Enumerable.Range(0, rows)
                .Select(i => (JsonNode)new JsonObject
                {
                    ["id"] = $"row_{i}",
                    ["cells"] = new JsonObject { ["col_a"] = new string('x', RowCellLength) },
                })
                .ToArray()),
        };
        var item = new JsonObject
        {
            ["id"] = PageId,
            ["type"] = "spreadsheet",
            ["title"] = "Daily log",
            ["content"] = schema.ToJsonString(),
        };
        _api.Respond($"/api/drive/{PageId}", item.ToJsonString());
    }

    private static async Task<(int Code, string Text)> CaptureStderrAsync(Func<Task<int>> action)
    {
        var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        try
        {
            var code = await action();
            return (code, stderr.ToString());
        }
        finally
        {
            Console.SetError(previous);
        }
    }
}
