namespace StartDown.Runner;

/// <summary>
/// Runs <see cref="StartupRunner"/> without assigning a WinForms MainForm. A hidden dispatcher
/// posts startup work to the WinForms message queue, so SetWinEventHook is installed only after
/// the thread message loop is processing messages. Completion explicitly calls ExitThread.
/// </summary>
internal sealed class RunnerApplicationContext : ApplicationContext
{
    private readonly StartupRunner _runner;
    private readonly Form? _statusWindow;
    private readonly bool _keepStatusOpenAfterCompletion;
    private Control? _dispatcher;
    private bool _started;
    private bool _exiting;
    private bool _exitRequested;
    private bool _statusWindowClosed;

    public RunnerApplicationContext(
        StartupRunner runner,
        Form? statusWindow = null,
        bool keepStatusOpenAfterCompletion = false)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _statusWindow = statusWindow;
        _keepStatusOpenAfterCompletion = keepStatusOpenAfterCompletion;

        _runner.Completed += OnRunnerCompleted;
        _dispatcher = new Control();
        _ = _dispatcher.Handle;
        _dispatcher.BeginInvoke(OnMessageLoopReady);

        if (_statusWindow is not null)
        {
            _statusWindow.FormClosed += OnStatusWindowClosed;
        }
    }

    public StartupRunner Runner => _runner;

    public int ExitCode { get; private set; }

    /// <summary>
    /// Stops an active run and exits the message loop. This method is intended to be called
    /// by the optional status window on the UI thread.
    /// </summary>
    public void RequestExit(string? reason = null)
    {
        if (_exiting)
        {
            return;
        }

        _exitRequested = true;
        if (!_runner.IsTerminal && _started)
        {
            _runner.Cancel(reason ?? "Stopped from the status window.");
            return;
        }

        Shutdown();
    }

    private void OnMessageLoopReady()
    {
        if (_exiting)
        {
            return;
        }

        _started = true;
        if (_statusWindow is not null && !_statusWindow.IsDisposed)
        {
            _statusWindow.Show();
        }

        try
        {
            _runner.Start();
        }
        catch
        {
            // StartupRunner converts operational failures into a terminal snapshot. Only a
            // programming/lifecycle exception should reach here, and the safest response is
            // to tear down the otherwise invisible message loop.
            ExitCode = 2;
            Shutdown();
        }
    }

    private void OnRunnerCompleted(object? sender, RunnerCompletedEventArgs args)
    {
        ExitCode = args.ExitCode;
        if (!_exitRequested &&
            _keepStatusOpenAfterCompletion &&
            !_statusWindowClosed &&
            _statusWindow is { IsDisposed: false })
        {
            return;
        }

        Shutdown();
    }

    private void OnStatusWindowClosed(object? sender, FormClosedEventArgs args)
    {
        _statusWindowClosed = true;
        if (!_runner.IsTerminal)
        {
            _runner.Cancel("The status window was closed.");
            return;
        }

        Shutdown();
    }

    private void Shutdown()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        if (_dispatcher is not null)
        {
            _dispatcher.Dispose();
            _dispatcher = null;
        }
        _runner.Completed -= OnRunnerCompleted;

        if (_statusWindow is not null)
        {
            _statusWindow.FormClosed -= OnStatusWindowClosed;
            if (!_statusWindow.IsDisposed)
            {
                _statusWindow.Close();
                _statusWindow.Dispose();
            }
        }

        _runner.Dispose();
        base.ExitThreadCore();
    }
}
