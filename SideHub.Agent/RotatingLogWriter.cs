using System.Text;

namespace SideHub.Agent;

/// <summary>
/// A TextWriter that writes to a log file with automatic rotation.
/// When the log file exceeds <see cref="MaxFileSizeBytes"/>, it is rotated:
///   .log -> .log.1 -> .log.2 -> ... -> .log.{MaxArchiveCount} (deleted)
/// </summary>
public sealed class RotatingLogWriter : TextWriter
{
    private const long DefaultMaxFileSize = 10 * 1024 * 1024; // 10 MB
    private const int DefaultMaxArchiveCount = 3;
    private const int CheckIntervalWrites = 100; // Check file size every N writes

    private readonly string _logFilePath;
    private readonly long _maxFileSizeBytes;
    private readonly int _maxArchiveCount;
    private readonly object _lock = new();

    private StreamWriter _writer;
    private int _writesSinceCheck;
    private bool _disposed;

    public long MaxFileSizeBytes => _maxFileSizeBytes;
    public int MaxArchiveCount => _maxArchiveCount;

    public override Encoding Encoding => Encoding.UTF8;

    public RotatingLogWriter(
        string logFilePath,
        long maxFileSizeBytes = DefaultMaxFileSize,
        int maxArchiveCount = DefaultMaxArchiveCount)
    {
        _logFilePath = logFilePath;
        _maxFileSizeBytes = maxFileSizeBytes;
        _maxArchiveCount = maxArchiveCount;

        EnsureDirectory();
        _writer = OpenWriter();
    }

    public override void Write(char value)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _writer.Write(value);
            IncrementAndMaybeRotate();
        }
    }

    public override void Write(string? value)
    {
        if (value == null) return;
        lock (_lock)
        {
            if (_disposed) return;
            _writer.Write(value);
            IncrementAndMaybeRotate();
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _writer.WriteLine(value);
            IncrementAndMaybeRotate();
        }
    }

    public override void Flush()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _writer.Flush();
        }
    }

    /// <summary>
    /// Returns the total size of the current log file plus all archive files.
    /// </summary>
    public static long GetTotalLogSize(string logFilePath, int maxArchiveCount = DefaultMaxArchiveCount)
    {
        long total = 0;

        if (File.Exists(logFilePath))
            total += new FileInfo(logFilePath).Length;

        for (int i = 1; i <= maxArchiveCount; i++)
        {
            var archivePath = $"{logFilePath}.{i}";
            if (File.Exists(archivePath))
                total += new FileInfo(archivePath).Length;
        }

        return total;
    }

    /// <summary>
    /// Lists all log files (current + archives) that exist on disk.
    /// </summary>
    public static List<string> GetAllLogFiles(string logFilePath, int maxArchiveCount = DefaultMaxArchiveCount)
    {
        var files = new List<string>();

        if (File.Exists(logFilePath))
            files.Add(logFilePath);

        for (int i = 1; i <= maxArchiveCount; i++)
        {
            var archivePath = $"{logFilePath}.{i}";
            if (File.Exists(archivePath))
                files.Add(archivePath);
        }

        return files;
    }

    private void IncrementAndMaybeRotate()
    {
        _writesSinceCheck++;
        if (_writesSinceCheck < CheckIntervalWrites)
            return;

        _writesSinceCheck = 0;
        RotateIfNeeded();
    }

    private void RotateIfNeeded()
    {
        try
        {
            _writer.Flush();

            var fileInfo = new FileInfo(_logFilePath);
            if (!fileInfo.Exists || fileInfo.Length < _maxFileSizeBytes)
                return;

            // Close current writer before rotating
            _writer.Dispose();

            // Rotate files: delete oldest, shift others
            var oldestArchive = $"{_logFilePath}.{_maxArchiveCount}";
            if (File.Exists(oldestArchive))
                File.Delete(oldestArchive);

            for (int i = _maxArchiveCount - 1; i >= 1; i--)
            {
                var source = $"{_logFilePath}.{i}";
                var dest = $"{_logFilePath}.{i + 1}";
                if (File.Exists(source))
                    File.Move(source, dest);
            }

            // Current log becomes .log.1
            File.Move(_logFilePath, $"{_logFilePath}.1");

            // Open fresh log file
            _writer = OpenWriter();
        }
        catch (Exception ex)
        {
            // If rotation fails, try to keep writing to the existing file
            try
            {
                if (_writer.BaseStream == null || !_writer.BaseStream.CanWrite)
                    _writer = OpenWriter();
            }
            catch
            {
                // Last resort: write to stderr so we don't lose the error completely
                Console.Error.WriteLine($"[SideHub] Log rotation failed: {ex.Message}");
            }
        }
    }

    private StreamWriter OpenWriter()
    {
        return new StreamWriter(_logFilePath, append: true) { AutoFlush = true };
    }

    private void EnsureDirectory()
    {
        var dir = Path.GetDirectoryName(_logFilePath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            lock (_lock)
            {
                _writer.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
