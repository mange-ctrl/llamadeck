using System.IO;
using System.Text;

namespace LlamaDeck.Core;

public enum LogLevel { Info, Server, Warn, Error, Success }

/// <summary>Samlar loggrader från appen och servern, och skriver valfritt till fil.</summary>
public sealed class LogBus
{
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private bool _saveToFile = true;

    public event Action<LogLevel, string>? Line;

    public bool SaveToFile
    {
        get => _saveToFile;
        set
        {
            lock (_sync)
            {
                _saveToFile = value;
                if (!value) CloseFileLocked();
            }
        }
    }

    public string? CurrentFile { get; private set; }

    public void Write(LogLevel level, string message)
    {
        var text = message ?? "";
        Line?.Invoke(level, text);

        if (!_saveToFile) return;
        try
        {
            lock (_sync)
            {
                if (_writer == null) OpenFileLocked();
                _writer?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-7}] {text}");
                _writer?.Flush();
            }
        }
        catch
        {
            // loggfilen är inte kritisk
        }
    }

    public void Info(string message) => Write(LogLevel.Info, message);
    public void Server(string message) => Write(LogLevel.Server, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);
    public void Error(string message) => Write(LogLevel.Error, message);
    public void Success(string message) => Write(LogLevel.Success, message);

    private void OpenFileLocked()
    {
        Directory.CreateDirectory(AppPaths.LogsDir);
        var name = $"llamadeck-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        CurrentFile = Path.Combine(AppPaths.LogsDir, name);
        _writer = new StreamWriter(new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false)) { AutoFlush = true };
    }

    private void CloseFileLocked()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }

    public void Shutdown()
    {
        lock (_sync) CloseFileLocked();
    }
}
