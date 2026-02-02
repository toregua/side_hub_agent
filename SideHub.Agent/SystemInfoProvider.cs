using System.Runtime.InteropServices;

namespace SideHub.Agent;

public static class SystemInfoProvider
{
    public static string GetDefaultShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "cmd";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "zsh";
        // Linux
        return "bash";
    }

    public static string[] GetAvailableShells()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var shells = new List<string> { "cmd", "powershell" };
            if (File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe") ||
                File.Exists(@"C:\Program Files (x86)\PowerShell\7\pwsh.exe"))
            {
                shells.Add("pwsh");
            }
            return shells.ToArray();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var shells = new List<string>();
            if (File.Exists("/bin/zsh")) shells.Add("zsh");
            if (File.Exists("/bin/bash")) shells.Add("bash");
            if (File.Exists("/bin/sh")) shells.Add("sh");
            return shells.Count > 0 ? shells.ToArray() : ["sh"];
        }

        // Linux
        {
            var shells = new List<string>();
            if (File.Exists("/bin/bash") || File.Exists("/usr/bin/bash")) shells.Add("bash");
            if (File.Exists("/bin/sh") || File.Exists("/usr/bin/sh")) shells.Add("sh");
            if (File.Exists("/bin/zsh") || File.Exists("/usr/bin/zsh")) shells.Add("zsh");
            return shells.Count > 0 ? shells.ToArray() : ["sh"];
        }
    }

    public static string GetOsPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        return "unknown";
    }
}
