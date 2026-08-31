using System.Collections.Concurrent;
using System.Diagnostics;
using StartDown.Core;
using StartDown.Infrastructure;
using StartDown.Interop;

namespace StartDown.Runner;

/// <summary>
/// Owns the short-lived startup run. All state transitions happen on the WinForms
/// message-loop thread; native callbacks only append to <see cref="_eventQueue"/>.
/// </summary>
internal sealed class StartupRunner : IDisposable
{
    private const int TickIntervalMilliseconds = 50;
    private const int RetryCaptureMilliseconds = 250;
    private const int CandidateResampleMilliseconds = 2_000;
    private const int StatusRefreshMilliseconds = 500;

    private readonly AppConfiguration _configuration;
    private readonly Guid? _onlyEntryId;
    private readonly AppLogger? _logger;
    private readonly WindowEventSource _windowEvents;
    private readonly WindowSnapshotProvider _snapshotProvider;
    private readonly WindowActionExecutor _actionExecutor;
    private readonly ProcessLauncher _processLauncher;
    private readonly ConcurrentQueue<QueuedWindowEvent> _eventQueue = new();
    private readonly Dictionary<nint, PendingWindow> _pendingWindows = [];
    private readonly List<EntryRuntime> _entries;
    private HashSet<Guid> _adoptedEntryIds = [];

    private System.Windows.Forms.Timer? _timer;
    private RunnerPhase _phase = RunnerPhase.Created;
    private string? _detail;
    private long _startedTimestamp;
    private long _globalDeadlineTimestamp;
    private long _lastStatusTimestamp;
    private uint _launchBoundaryEventTime;
    private bool _launchBoundarySet;
    private int _ownerThreadId;
    private int _acceptEvents;
    private int _disposed;
    private bool _completionRaised;

    public StartupRunner(
        AppConfiguration configuration,
        Guid? onlyEntryId = null,
        AppLogger? logger = null)
        : this(
            configuration,
            onlyEntryId,
            logger,
            new WindowEventSource(),
            new WindowSnapshotProvider(),
            new WindowActionExecutor(),
            new ProcessLauncher())
    {
    }

    internal StartupRunner(
        AppConfiguration configuration,
        Guid? onlyEntryId,
        AppLogger? logger,
        WindowEventSource windowEvents,
        WindowSnapshotProvider snapshotProvider,
        WindowActionExecutor actionExecutor,
        ProcessLauncher processLauncher)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _onlyEntryId = onlyEntryId;
        _logger = logger;
        _windowEvents = windowEvents;
        _snapshotProvider = snapshotProvider;
        _actionExecutor = actionExecutor;
        _processLauncher = processLauncher;

        var validation = ConfigurationValidator.NormalizeAndValidate(configuration);
        _configuration = validation.Configuration;
        ValidationIssues = validation.Issues;
        _entries = _configuration.Entries
            .Where(entry => onlyEntryId is null || entry.Id == onlyEntryId.Value)
            .Select(entry => new EntryRuntime(entry))
            .ToList();

        CurrentStatus = CreateStatus(Stopwatch.GetTimestamp());
    }

    public event EventHandler<RunnerStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<RunnerCompletedEventArgs>? Completed;

    public IReadOnlyList<ConfigurationValidationIssue> ValidationIssues { get; }

    public RunnerStatus CurrentStatus { get; private set; }

    public bool IsTerminal => CurrentStatus.IsTerminal;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_phase != RunnerPhase.Created)
        {
            throw new InvalidOperationException("The startup runner can only be started once.");
        }

        _ownerThreadId = Environment.CurrentManagedThreadId;
        _startedTimestamp = Stopwatch.GetTimestamp();
        _globalDeadlineTimestamp = AddDuration(
            _startedTimestamp,
            TimeSpan.FromSeconds(_configuration.GlobalTimeoutSeconds));
        _phase = RunnerPhase.Starting;
        PublishStatus(force: true);

        try
        {
            StartCore();
        }
        catch (Exception exception)
        {
            FailFatal(exception);
        }
    }

    public void Cancel(string? reason = null)
    {
        EnsureOwnerThread();
        if (_completionRaised)
        {
            return;
        }

        _detail = string.IsNullOrWhiteSpace(reason) ? "The run was cancelled." : reason;
        foreach (var entry in _entries.Where(entry => !entry.IsTerminal))
        {
            entry.State = EntryRunState.Cancelled;
            entry.Detail = _detail;
        }

        Complete(RunnerPhase.Cancelled);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopRuntimeResources();
        GC.SuppressFinalize(this);
    }

    private void StartCore()
    {
        if (ValidationIssues.Count != 0)
        {
            var message = string.Join("; ", ValidationIssues.Select(issue => $"{issue.Path}: {issue.Message}"));
            foreach (var entry in _entries.Where(entry => !entry.IsTerminal))
            {
                entry.State = EntryRunState.InvalidConfiguration;
                entry.Detail = message;
            }

            _detail = message;
            _logger?.Error($"Runner configuration is invalid: {message}");
            Complete(RunnerPhase.Faulted);
            return;
        }

        if (_onlyEntryId is not null && _entries.Count == 0)
        {
            _detail = $"Entry {_onlyEntryId} was not found.";
            _logger?.Error(_detail);
            Complete(RunnerPhase.Faulted);
            return;
        }

        if (_entries.All(entry => entry.IsTerminal))
        {
            _detail = "There are no enabled entries to run.";
            Complete(RunnerPhase.Completed);
            return;
        }

        _windowEvents.WindowEventReceived += OnWindowEventReceived;
        Volatile.Write(ref _acceptEvents, 1);

        // WindowEventSource.Start installs every required hook before EnumWindows. A failure
        // throws before any target process is launched, which preserves hook-before-launch.
        var baselineWindows = _windowEvents.Start();
        _phase = RunnerPhase.HooksReady;
        PublishStatus(force: true);

        _phase = RunnerPhase.Reconciling;
        var now = Stopwatch.GetTimestamp();
        _adoptedEntryIds = ReconcileExistingProcesses(now);

        // Baseline handles may only satisfy entries that deliberately adopted an existing
        // process. They are never allowed to satisfy an entry that will be launched below.
        foreach (var hwnd in baselineWindows)
        {
            AddPendingWindow(hwnd, now, _adoptedEntryIds);
        }

        _phase = RunnerPhase.Launching;
        PublishStatus(force: true);

        // WinEventSource dispatches through the thread pool. Its native event timestamp lets
        // us keep an event that happened before this boundary scoped to adopted entries even
        // if its managed callback is delayed until after Process.Start.
        _launchBoundaryEventTime = unchecked((uint)Environment.TickCount);
        _launchBoundarySet = true;
        LaunchPendingEntries();

        _timer = new System.Windows.Forms.Timer
        {
            Interval = TickIntervalMilliseconds,
        };
        _timer.Tick += OnTick;
        _timer.Start();

        _phase = RunnerPhase.Monitoring;
        _logger?.Info("Startup runner is monitoring windows.");
        PublishStatus(force: true);

        // Launch failures and already-running skips can make every entry terminal without
        // waiting for the first timer message.
        CompleteIfAllEntriesTerminal();
    }

    private HashSet<Guid> ReconcileExistingProcesses(long now)
    {
        var adopted = new HashSet<Guid>();
        var processCache = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var runtime in _entries.Where(entry => entry.State == EntryRunState.Pending))
        {
            if (!processCache.TryGetValue(runtime.Entry.ExecutablePath, out var processIds))
            {
                processIds = _processLauncher.FindRunningExactPath(runtime.Entry.ExecutablePath);
                processCache[runtime.Entry.ExecutablePath] = processIds;
            }

            if (processIds.Count == 0)
            {
                continue;
            }

            runtime.ExistingProcessIds = processIds.ToArray();
            if (runtime.Entry.ExistingInstancePolicy == ExistingInstancePolicy.Skip)
            {
                runtime.State = EntryRunState.SkippedAlreadyRunning;
                runtime.Detail = $"Already running as PID {string.Join(", ", processIds)}; skipped.";
                _logger?.Info($"{runtime.Entry.Name}: {runtime.Detail}");
                continue;
            }

            runtime.State = EntryRunState.Watching;
            runtime.DeadlineTimestamp = AddDuration(now, TimeSpan.FromSeconds(runtime.Entry.TimeoutSeconds));
            runtime.Detail = $"Adopted existing PID {string.Join(", ", processIds)}.";
            adopted.Add(runtime.Entry.Id);
            _logger?.Info($"{runtime.Entry.Name}: {runtime.Detail}");
        }

        return adopted;
    }

    private void LaunchPendingEntries()
    {
        foreach (var runtime in _entries.Where(entry => entry.State == EntryRunState.Pending))
        {
            var now = Stopwatch.GetTimestamp();
            runtime.State = EntryRunState.Launching;
            runtime.DeadlineTimestamp = AddDuration(now, TimeSpan.FromSeconds(runtime.Entry.TimeoutSeconds));
            PublishStatus(force: true);

            var result = _processLauncher.Launch(
                runtime.Entry.ExecutablePath,
                runtime.Entry.Arguments,
                runtime.Entry.WorkingDirectory);

            if (!result.Succeeded)
            {
                runtime.State = EntryRunState.LaunchFailed;
                runtime.Detail = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? $"Launch failed with Win32 error {result.Win32Error}."
                    : result.ErrorMessage;
                _logger?.Error($"{runtime.Entry.Name}: {runtime.Detail}");
                continue;
            }

            runtime.LaunchedProcessId = result.ProcessId;
            runtime.State = EntryRunState.Watching;
            runtime.Detail = result.ProcessId is int processId
                ? $"Launched as PID {processId}; waiting for a matching window."
                : "Launched; waiting for a matching window.";
            _logger?.Info($"{runtime.Entry.Name}: {runtime.Detail}");
        }
    }

    private void OnWindowEventReceived(object? sender, WindowEventArgs args)
    {
        if (Volatile.Read(ref _acceptEvents) == 0)
        {
            return;
        }

        _eventQueue.Enqueue(new QueuedWindowEvent(
            args.Hwnd,
            Stopwatch.GetTimestamp(),
            args.EventTimeMilliseconds));
    }

    private void OnTick(object? sender, EventArgs args)
    {
        EnsureOwnerThread();
        try
        {
            TickCore();
        }
        catch (Exception exception)
        {
            FailFatal(exception);
        }
    }

    private void TickCore()
    {
        var now = Stopwatch.GetTimestamp();

        if (now >= _globalDeadlineTimestamp)
        {
            foreach (var entry in _entries.Where(entry => !entry.IsTerminal))
            {
                entry.State = EntryRunState.GlobalTimedOut;
                entry.Detail = "The global runner timeout elapsed.";
            }

            _detail = "The global runner timeout elapsed.";
            _logger?.Warning(_detail);
            Complete(RunnerPhase.Completed);
            return;
        }

        while (_eventQueue.TryDequeue(out var windowEvent))
        {
            var eligibleEntries = _launchBoundarySet &&
                                  EventOccurredBefore(
                                      windowEvent.NativeEventTimeMilliseconds,
                                      _launchBoundaryEventTime)
                ? _adoptedEntryIds
                : null;
            AddPendingWindow(windowEvent.Hwnd, windowEvent.Timestamp, eligibleEntries);
        }

        EvaluatePendingWindows(now);
        ExpireEntries(now);

        if (!CompleteIfAllEntriesTerminal())
        {
            PublishStatus(force: HasElapsed(_lastStatusTimestamp, now, StatusRefreshMilliseconds));
        }
    }

    private void AddPendingWindow(nint hwnd, long timestamp, IReadOnlySet<Guid>? eligibleEntryIds)
    {
        if (hwnd == 0)
        {
            return;
        }

        if (_pendingWindows.TryGetValue(hwnd, out var existing))
        {
            existing.LastSignalTimestamp = timestamp;

            // A real post-hook event broadens a baseline candidate to every active entry.
            if (eligibleEntryIds is null)
            {
                existing.EligibleEntryIds = null;
            }

            return;
        }

        _pendingWindows.Add(
            hwnd,
            new PendingWindow(
                hwnd,
                timestamp,
                eligibleEntryIds is null ? null : new HashSet<Guid>(eligibleEntryIds)));
    }

    private void EvaluatePendingWindows(long now)
    {
        if (_pendingWindows.Count == 0)
        {
            return;
        }

        List<nint>? remove = null;
        foreach (var candidate in _pendingWindows.Values)
        {
            var eligibleEntries = _entries
                .Where(entry => entry.State == EntryRunState.Watching)
                .Where(entry => candidate.EligibleEntryIds is null || candidate.EligibleEntryIds.Contains(entry.Entry.Id))
                .ToArray();

            if (eligibleEntries.Length == 0)
            {
                (remove ??= []).Add(candidate.Hwnd);
                continue;
            }

            var dueEntries = eligibleEntries
                .Where(entry => now >= AddDuration(
                    candidate.FirstSignalTimestamp,
                    TimeSpan.FromMilliseconds(entry.Entry.ActionDelayMilliseconds)))
                .ToArray();

            if (dueEntries.Length == 0 ||
                !HasElapsed(candidate.LastCaptureTimestamp, now, RetryCaptureMilliseconds))
            {
                continue;
            }

            candidate.LastCaptureTimestamp = now;
            if (_snapshotProvider.TryCapture(candidate.Hwnd, out var snapshot) && snapshot is not null)
            {
                EvaluateSnapshot(snapshot, dueEntries);
            }

            var maximumDelay = eligibleEntries.Max(entry => entry.Entry.ActionDelayMilliseconds);
            var expiryBase = Math.Max(
                AddDuration(candidate.FirstSignalTimestamp, TimeSpan.FromMilliseconds(maximumDelay)),
                candidate.LastSignalTimestamp);
            if (now >= AddDuration(expiryBase, TimeSpan.FromMilliseconds(CandidateResampleMilliseconds)))
            {
                (remove ??= []).Add(candidate.Hwnd);
            }
        }

        if (remove is null)
        {
            return;
        }

        foreach (var hwnd in remove)
        {
            _pendingWindows.Remove(hwnd);
        }
    }

    private void EvaluateSnapshot(WindowSnapshot snapshot, IReadOnlyList<EntryRuntime> entries)
    {
        var identity = new WindowIdentity(snapshot.Hwnd, snapshot.ProcessId);
        foreach (var runtime in entries)
        {
            if (runtime.State != EntryRunState.Watching || runtime.HandledWindows.Contains(identity))
            {
                continue;
            }

            if (!RuleMatcher.Matches(runtime.Entry, snapshot))
            {
                continue;
            }

            var result = _actionExecutor.Execute(snapshot.Hwnd, runtime.Entry.Action);
            if (!result.Succeeded)
            {
                runtime.Detail = result.ErrorMessage is null
                    ? $"Could not apply {runtime.Entry.Action}: {result.Status}."
                    : $"Could not apply {runtime.Entry.Action}: {result.ErrorMessage}";
                if (runtime.ReportedActionFailures.Add(identity))
                {
                    _logger?.Warning($"{runtime.Entry.Name}: {runtime.Detail}");
                }
                continue;
            }

            runtime.ReportedActionFailures.Remove(identity);
            runtime.HandledWindows.Add(identity);
            runtime.MatchedWindows++;
            runtime.Detail = $"Applied {runtime.Entry.Action} to window 0x{snapshot.Hwnd:X}; " +
                             $"match {runtime.MatchedWindows}/{runtime.Entry.ExpectedMatches}.";
            _logger?.Info($"{runtime.Entry.Name}: {runtime.Detail}");

            if (runtime.MatchedWindows >= runtime.Entry.ExpectedMatches)
            {
                runtime.State = EntryRunState.Succeeded;
                runtime.DeadlineTimestamp = null;
            }
        }
    }

    private void ExpireEntries(long now)
    {
        foreach (var runtime in _entries.Where(entry => entry.State == EntryRunState.Watching))
        {
            if (runtime.DeadlineTimestamp is not long deadline || now < deadline)
            {
                continue;
            }

            runtime.State = EntryRunState.TimedOut;
            runtime.Detail = $"Timed out after {runtime.Entry.TimeoutSeconds} seconds with " +
                             $"{runtime.MatchedWindows}/{runtime.Entry.ExpectedMatches} matches.";
            _logger?.Warning($"{runtime.Entry.Name}: {runtime.Detail}");
        }
    }

    private bool CompleteIfAllEntriesTerminal()
    {
        if (_entries.Any(entry => !entry.IsTerminal))
        {
            return false;
        }

        _detail ??= BuildCompletionDetail();
        Complete(RunnerPhase.Completed);
        return true;
    }

    private void FailFatal(Exception exception)
    {
        if (_completionRaised)
        {
            return;
        }

        _detail = exception.Message;
        foreach (var entry in _entries.Where(entry => !entry.IsTerminal))
        {
            entry.State = EntryRunState.Aborted;
            entry.Detail = $"Runner failed: {exception.Message}";
        }

        _logger?.Error($"Runner failed: {exception}");
        Complete(RunnerPhase.Faulted);
    }

    private void Complete(RunnerPhase phase)
    {
        if (_completionRaised)
        {
            return;
        }

        _completionRaised = true;
        _phase = phase;
        StopRuntimeResources();
        PublishStatus(force: true);

        var exitCode = CalculateExitCode();
        RaiseCompleted(new RunnerCompletedEventArgs(CurrentStatus, exitCode));
    }

    private void StopRuntimeResources()
    {
        Volatile.Write(ref _acceptEvents, 0);

        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer.Dispose();
            _timer = null;
        }

        _windowEvents.WindowEventReceived -= OnWindowEventReceived;
        _windowEvents.Dispose();
        _eventQueue.Clear();
        _pendingWindows.Clear();
    }

    private void PublishStatus(bool force)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && !HasElapsed(_lastStatusTimestamp, now, StatusRefreshMilliseconds))
        {
            return;
        }

        _lastStatusTimestamp = now;
        CurrentStatus = CreateStatus(now);
        RaiseStatusChanged(new RunnerStatusChangedEventArgs(CurrentStatus));
    }

    private RunnerStatus CreateStatus(long now)
    {
        var elapsed = _startedTimestamp == 0
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(_startedTimestamp, now);
        TimeSpan? globalRemaining = _startedTimestamp == 0 || _completionRaised
            ? null
            : Remaining(_globalDeadlineTimestamp, now);

        var entries = _entries
            .Select(runtime => new EntryRunStatus(
                runtime.Entry.Id,
                runtime.Entry.Name,
                runtime.State,
                runtime.MatchedWindows,
                runtime.Entry.ExpectedMatches,
                runtime.LaunchedProcessId,
                runtime.ExistingProcessIds.ToArray(),
                runtime.DeadlineTimestamp is long deadline && !runtime.IsTerminal
                    ? Remaining(deadline, now)
                    : null,
                runtime.Detail))
            .ToArray();

        return new RunnerStatus(_phase, elapsed, globalRemaining, entries, _detail);
    }

    private string BuildCompletionDetail()
    {
        var succeeded = _entries.Count(entry => entry.State == EntryRunState.Succeeded);
        var skipped = _entries.Count(entry => entry.State is EntryRunState.Disabled or EntryRunState.SkippedAlreadyRunning);
        var failed = _entries.Count - succeeded - skipped;
        return $"Run complete: {succeeded} succeeded, {skipped} skipped, {failed} failed.";
    }

    private int CalculateExitCode()
    {
        if (_phase == RunnerPhase.Faulted)
        {
            return 2;
        }

        if (_phase == RunnerPhase.Cancelled)
        {
            return 3;
        }

        return _entries.Any(entry => entry.State is
            EntryRunState.LaunchFailed or
            EntryRunState.TimedOut or
            EntryRunState.GlobalTimedOut or
            EntryRunState.InvalidConfiguration or
            EntryRunState.Aborted)
            ? 1
            : 0;
    }

    private void EnsureOwnerThread()
    {
        if (_ownerThreadId != 0 && Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("Runner state must be changed on its WinForms message-loop thread.");
        }
    }

    private void RaiseStatusChanged(RunnerStatusChangedEventArgs args)
    {
        var handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<RunnerStatusChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                Trace.TraceError("Runner status subscriber failed: {0}", exception);
            }
        }
    }

    private void RaiseCompleted(RunnerCompletedEventArgs args)
    {
        var handlers = Completed;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<RunnerCompletedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                Trace.TraceError("Runner completion subscriber failed: {0}", exception);
            }
        }
    }

    private static long AddDuration(long timestamp, TimeSpan duration) =>
        timestamp + (long)(duration.TotalSeconds * Stopwatch.Frequency);

    private static bool HasElapsed(long earlier, long now, int milliseconds)
    {
        if (earlier == 0)
        {
            return true;
        }

        return Stopwatch.GetElapsedTime(earlier, now) >= TimeSpan.FromMilliseconds(milliseconds);
    }

    private static TimeSpan Remaining(long deadline, long now) =>
        deadline <= now
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((deadline - now) / (double)Stopwatch.Frequency);

    private static bool EventOccurredBefore(uint eventTime, uint boundary) =>
        unchecked((int)(eventTime - boundary)) < 0;

    private sealed record QueuedWindowEvent(
        nint Hwnd,
        long Timestamp,
        uint NativeEventTimeMilliseconds);

    private sealed class PendingWindow(
        nint hwnd,
        long firstSignalTimestamp,
        HashSet<Guid>? eligibleEntryIds)
    {
        public nint Hwnd { get; } = hwnd;

        public long FirstSignalTimestamp { get; } = firstSignalTimestamp;

        public long LastSignalTimestamp { get; set; } = firstSignalTimestamp;

        public long LastCaptureTimestamp { get; set; }

        /// <summary>
        /// Null means every watching entry. A non-null set is used for the initial baseline,
        /// which may only be evaluated against entries that explicitly adopted a process.
        /// </summary>
        public HashSet<Guid>? EligibleEntryIds { get; set; } = eligibleEntryIds;
    }
}
