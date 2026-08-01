using System.Text;
using Microsoft.Extensions.Logging;

namespace Ngino.Client;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const long DefaultMaxFileSizeBytes = 5L * 1024 * 1024;
    private const string LogFileName = "ngino-client.log";
    private const string RotatedLogFileName = "ngino-client.log.1";

    private readonly string _directory;
    private readonly long _maxFileSizeBytes;
    private readonly object _lock = new();
    private StreamWriter _writer = null!;
    private string _currentFile = null!;

    public FileLoggerProvider(string directory, long maxFileSizeBytes = DefaultMaxFileSizeBytes)
    {
        _directory = directory;
        _maxFileSizeBytes = maxFileSizeBytes;
        Directory.CreateDirectory(directory);
        OpenFile();
    }

    public string LogDirectory => _directory;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void WriteLog(DateTime timestamp, LogLevel level, string category, string message)
    {
        lock (_lock)
        {
            var line = $"{timestamp:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}: {message}";
            if (_writer.BaseStream.Length + line.Length + 2 > _maxFileSizeBytes)
            {
                RotateFile();
            }

            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }

    private void OpenFile()
    {
        _currentFile = Path.Combine(_directory, LogFileName);
        _writer = new StreamWriter(
            new FileStream(_currentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    private void RotateFile()
    {
        _writer.Dispose();

        var rotatedFile = Path.Combine(_directory, RotatedLogFileName);
        try
        {
            File.Delete(rotatedFile);
            if (File.Exists(_currentFile))
            {
                File.Move(_currentFile, rotatedFile);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        OpenFile();
    }
}

internal sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Trace;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (exception is not null)
        {
            message += Environment.NewLine + exception;
        }

        provider.WriteLog(DateTime.Now, logLevel, category, message);
    }
}
