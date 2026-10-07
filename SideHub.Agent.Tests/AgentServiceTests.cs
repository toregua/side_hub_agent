using System.Xml.Linq;

namespace SideHub.Agent.Tests;

public class AgentServiceTests
{
    [Fact]
    public void Service_name_is_readable_and_unique_per_folder()
    {
        var a = AgentService.ServiceName("/home/me/My Project");
        var b = AgentService.ServiceName("/srv/other/My Project");

        Assert.StartsWith("sidehub-agent-my-project-", a);
        Assert.Matches("^sidehub-agent-my-project-[0-9a-f]{8}$", a);
        Assert.NotEqual(a, b);
        Assert.Equal(a, AgentService.ServiceName("/home/me/My Project/"));
    }

    [Fact]
    public void Service_name_without_a_usable_folder_name_is_the_hash()
    {
        Assert.Matches("^sidehub-agent-[0-9a-f]{8}$", AgentService.ServiceName("/home/me/项目"));
    }

    [Fact]
    public void Systemd_unit_runs_the_daemon_from_the_folder_with_the_user_path()
    {
        var unit = AgentService.SystemdUnit("/home/me/app", "/usr/local/lib/sidehub-agent/sidehub-agent", "/home/me/.nvm/bin:/usr/bin", allowRoot: false);

        Assert.Contains("WorkingDirectory=/home/me/app\n", unit);
        Assert.Contains("ExecStart=\"/usr/local/lib/sidehub-agent/sidehub-agent\" \"--foreground-daemon\" " +
                        "\"/home/me/app/.sidehub/run/sidehub-agent.log\" \"/home/me/app/.sidehub/run/sidehub-agent.pid\"\n", unit);
        Assert.Contains("Environment=\"PATH=/home/me/.nvm/bin:/usr/bin\"\n", unit);
        Assert.Contains("KillSignal=SIGINT", unit);
        Assert.Contains("WantedBy=default.target", unit);
        Assert.DoesNotContain(RootPolicy.AllowFlag, unit);
    }

    [Fact]
    public void Systemd_unit_escapes_specifiers_and_quotes()
    {
        var unit = AgentService.SystemdUnit("/home/me/100%", "/opt/a \"b\"/sidehub-agent", null, allowRoot: true);

        Assert.Contains("WorkingDirectory=/home/me/100%%\n", unit);
        Assert.Contains("ExecStart=\"/opt/a \\\"b\\\"/sidehub-agent\"", unit);
        Assert.Contains($"sidehub-agent.pid\" {RootPolicy.AllowFlag}\n", unit);
        Assert.DoesNotContain("Environment=", unit);
    }

    [Fact]
    public void Launchd_plist_is_valid_xml_with_escaped_values()
    {
        var plist = AgentService.LaunchdPlist("io.sidehub.agent.x", "/Users/me/R&D", "/usr/local/bin/sidehub-agent", "/opt/homebrew/bin:/usr/bin", allowRoot: false);

        var doc = XDocument.Parse(plist);
        var strings = doc.Descendants("string").Select(e => e.Value).ToList();
        Assert.Contains("/Users/me/R&D", strings);
        Assert.Contains("/Users/me/R&D/.sidehub/run/sidehub-agent.pid", strings);
        Assert.Contains("/opt/homebrew/bin:/usr/bin", strings);
        Assert.DoesNotContain(RootPolicy.AllowFlag, strings);
    }

    [Fact]
    public void Scheduled_task_starts_the_daemon_from_the_folder_at_logon()
    {
        var xml = AgentService.ScheduledTaskXml("PC\\me", @"C:\Users\me\R&D", @"C:\Users\me\AppData\Local\Programs\sidehub-agent\sidehub-agent.exe");

        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("PC\\me", doc.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
        var exec = doc.Descendants(ns + "Exec").Single();
        Assert.Equal("start -d", exec.Element(ns + "Arguments")!.Value);
        Assert.Equal(@"C:\Users\me\R&D", exec.Element(ns + "WorkingDirectory")!.Value);
    }
}
