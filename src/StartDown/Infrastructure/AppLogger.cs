using System.Collections.Concurrent;

namespace StartDown.Infrastructure;

internal enum LogLevel
{
    Information,
    Warning,
    Error
}

internal sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message)
{
    public override string ToString() => $"{Timestamp:HH:mm:ss.fff} [{Level}] {Message}";
}

internal sealed class AppLogger : IDisposable
{
    private readonly BlockingCollection<LogEntry> _pending = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _writer;
    private bool _disposed;

    public AppLogger()
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        _writer = Task.Run(WriteLoop);
    }

    public event EventHandler<LogEntry>? EntryWritten;

    public void Info(string message) => Write(LogLevel.Information, message);

    public void Warning(string message) => Write(LogLevel.Warning, message);

    public void Error(string message) => Write(LogLevel.Error, message);

    private void Write(LogLevel level, string message)
    {
        if (_disposed)
        {
            return;
        }

        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        try
        {
            _pending.Add(entry);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            EntryWritten?.Invoke(this, entry);
        }
        catch
        {
            // A status-window subscriber must not break the startup state machine.
        }
    }

    private void WriteLoop()
    {
        var file = Path.Combine(
            AppPaths.LogDirectory,
            $"startdown-{DateTime.Now:yyyyMMdd}-{Environment.ProcessId}.log");

        try
        {
            foreach (var entry in _pending.GetConsumingEnumerable(_stopping.Token))
            {
                try
                {
                    File.AppendAllText(file, entry + Environment.NewLine);
                }
                catch
                {
                    // A transient lock or I/O failure should drop one line, not permanently
                    // stop the writer while producers keep queueing messages.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pending.CompleteAdding();
        if (!_writer.Wait(TimeSpan.FromSeconds(1)))
        {
            _stopping.Cancel();
            try { _writer.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        }

        _pending.Dispose();
        _stopping.Dispose();
    }
}
