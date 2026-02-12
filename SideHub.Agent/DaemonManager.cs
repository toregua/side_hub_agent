using System.Diagnostics;

namespace SideHub.Agent;

/// <summary>
/// Manages daemon mode: PID file, log file, and process lifecycle.
/// Files are stored in .sidehub/run/ relative to the working directory.
/// </summary>
public class DaemonManager
{
    private readonly string _runDirectory;
    private readonly string _pidFile;
    private readonly string _logFile;

    public string LogFile => _logFile;
    public string PidFile => _pidFile;

    public DaemonManager(string baseDirectory)
    {
        _runDirectory = Path.Combine(baseDirectory, ".sidehub", "run");
        _pidFile = Path.Combine(_runDirectory, "sidehub-agent.pid");
        _logFile = Path.Combine(_runDirectory, "sidehub-agent.log");
    }

    public void EnsureRunDirectory()
    {
        if (!Directory.Exists(_runDirectory))
        {
            Directory.CreateDirectory(_runDirectory);
        }
    }

    public void WritePidFile(int pid)
    {
        EnsureRunDirectory();
        File.WriteAllText(_pidFile, pid.ToString());
    }

    public void RemovePidFile()
    {
        if (File.Exists(_pidFile))
        {
            File.Delete(_pidFile);
        }
    }

    public int? ReadPid()
    {
        if (!File.Exists(_pidFile))
            return null;

        var content = File.ReadAllText(_pidFile).Trim();
        return int.TryParse(content, out var pid) ? pid : null;
    }

    public bool IsRunning()
    {
        var pid = ReadPid();
        if (pid == null)
            return false;

        try
        {
            var process = Process.GetProcessById(pid.Value);
            // Check if it's actually our process (not a recycled PID)
            return !process.HasExited && process.ProcessName.Contains("sidehub-agent", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // Process doesn't exist
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public Process? GetRunningProcess()
    {
        var pid = ReadPid();
        if (pid == null)
            return null;

        try
        {
            var process = Process.GetProcessById(pid.Value);
            if (!process.HasExited)
                return process;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }

        return null;
    }

    public bool StopDaemon()
    {
        var process = GetRunningProcess();
        if (process == null)
        {
            RemovePidFile();
            return false;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            RemovePidFile();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public RotatingLogWriter CreateLogWriter()
    {
        EnsureRunDirectory();
        return new RotatingLogWriter(_logFile);
    }

    public FileStream? OpenLogForReading()
    {
        if (!File.Exists(_logFile))
            return null;

        return new FileStream(_logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }
}
