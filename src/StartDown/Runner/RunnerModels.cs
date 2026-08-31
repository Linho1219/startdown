using StartDown.Core;

namespace StartDown.Runner;

internal enum RunnerPhase
{
    Created,
    Starting,
    HooksReady,
    Reconciling,
    Launching,
    Monitoring,
    Completed,
    Faulted,
    Cancelled,
}

internal enum EntryRunState
{
    Pending,
    Disabled,
    Launching,
    Watching,
    Succeeded,
    SkippedAlreadyRunning,
    LaunchFailed,
    TimedOut,
    GlobalTimedOut,
    InvalidConfiguration,
    Aborted,
    Cancelled,
}

internal sealed record EntryRunStatus(
    Guid Id,
    string Name,
    EntryRunState State,
    int MatchedWindows,
    int ExpectedMatches,
    int? LaunchedProcessId,
    IReadOnlyList<int> ExistingProcessIds,
    TimeSpan? Remaining,
    string? Detail)
{
    public bool IsTerminal => EntryRuntime.IsTerminalState(State);
}

internal sealed record RunnerStatus(
    RunnerPhase Phase,
    TimeSpan Elapsed,
    TimeSpan? GlobalRemaining,
    IReadOnlyList<EntryRunStatus> Entries,
    string? Detail)
{
    public bool IsTerminal => Phase is RunnerPhase.Completed or RunnerPhase.Faulted or RunnerPhase.Cancelled;
}

internal sealed class RunnerStatusChangedEventArgs(RunnerStatus status) : EventArgs
{
    public RunnerStatus Status { get; } = status;
}

internal sealed class RunnerCompletedEventArgs(RunnerStatus status, int exitCode) : EventArgs
{
    public RunnerStatus Status { get; } = status;

    public int ExitCode { get; } = exitCode;
}

internal sealed class EntryRuntime
{
    public EntryRuntime(LaunchEntry entry)
    {
        Entry = entry;
        State = entry.Enabled ? EntryRunState.Pending : EntryRunState.Disabled;
    }

    public LaunchEntry Entry { get; }

    public EntryRunState State { get; set; }

    public int MatchedWindows { get; set; }

    public int? LaunchedProcessId { get; set; }

    public IReadOnlyList<int> ExistingProcessIds { get; set; } = Array.Empty<int>();

    public long? DeadlineTimestamp { get; set; }

    public string? Detail { get; set; }

    public HashSet<WindowIdentity> HandledWindows { get; } = [];

    public HashSet<WindowIdentity> ReportedActionFailures { get; } = [];

    public bool IsTerminal => IsTerminalState(State);

    public static bool IsTerminalState(EntryRunState state) => state is
        EntryRunState.Disabled or
        EntryRunState.Succeeded or
        EntryRunState.SkippedAlreadyRunning or
        EntryRunState.LaunchFailed or
        EntryRunState.TimedOut or
        EntryRunState.GlobalTimedOut or
        EntryRunState.InvalidConfiguration or
        EntryRunState.Aborted or
        EntryRunState.Cancelled;
}

internal readonly record struct WindowIdentity(nint Hwnd, int ProcessId);
