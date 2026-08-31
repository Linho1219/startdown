using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StartDown.Interop;

public enum WindowEventKind
{
    Shown,
    NameChanged,
}

public sealed class WindowEventArgs : EventArgs
{
    internal WindowEventArgs(
        nint hwnd,
        WindowEventKind kind,
        uint nativeEventType,
        uint eventThreadId,
        uint eventTimeMilliseconds)
    {
        Hwnd = hwnd;
        Kind = kind;
        NativeEventType = nativeEventType;
        EventThreadId = eventThreadId;
        EventTimeMilliseconds = eventTimeMilliseconds;
    }

    public nint Hwnd { get; }
    public WindowEventKind Kind { get; }
    public uint NativeEventType { get; }
    public uint EventThreadId { get; }
    public uint EventTimeMilliseconds { get; }
}

/// <summary>
/// Reports top-level window show and name-change events from other processes.
/// Call <see cref="Start"/> on a thread that continues to pump Windows messages.
/// </summary>
public sealed class WindowEventSource : IDisposable
{
    private readonly object _gate = new();
    private readonly NativeMethods.WinEventDelegate _showCallback;
    private readonly NativeMethods.WinEventDelegate _nameChangeCallback;

    private nint _showHook;
    private nint _nameChangeHook;
    private bool _started;
    private int _disposed;

    public WindowEventSource()
    {
        // These fields intentionally hold the delegates for the complete native-hook lifetime.
        _showCallback = OnShow;
        _nameChangeCallback = OnNameChange;
    }

    public event EventHandler<WindowEventArgs>? WindowEventReceived;

    public bool IsStarted
    {
        get
        {
            lock (_gate)
            {
                return _started;
            }
        }
    }

    /// <summary>
    /// Installs both hooks and then enumerates existing top-level windows. Installing before
    /// enumerating closes the race in which a target window appears during initial discovery.
    /// </summary>
    /// <returns>A point-in-time list of existing top-level window handles.</returns>
    public IReadOnlyList<nint> Start()
    {
        var installedNow = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (!_started)
            {
                InstallHooks();
                _started = true;
                installedNow = true;
            }
        }

        try
        {
            return EnumerateTopLevelWindows();
        }
        catch when (installedNow)
        {
            lock (_gate)
            {
                UninstallHooks();
            }

            throw;
        }
    }

    /// <summary>
    /// Enumerates all current desktop top-level windows through EnumWindows.
    /// </summary>
    public static IReadOnlyList<nint> EnumerateTopLevelWindows()
    {
        var windows = new List<nint>();
        NativeMethods.EnumWindowsDelegate callback = (hwnd, _) =>
        {
            if (hwnd != 0)
            {
                windows.Add(hwnd);
            }

            return true;
        };

        Marshal.SetLastPInvokeError(0);
        var succeeded = NativeMethods.EnumWindows(callback, 0);
        var error = Marshal.GetLastWin32Error();
        GC.KeepAlive(callback);

        if (!succeeded)
        {
            if (error != 0)
            {
                throw new Win32Exception(error, "EnumWindows failed while scanning existing windows.");
            }

            throw new InvalidOperationException("EnumWindows stopped before completing the initial scan.");
        }

        return windows.AsReadOnly();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            UninstallHooks();
        }

        GC.SuppressFinalize(this);
    }

    private void InstallHooks()
    {
        const uint flags = NativeMethods.WineventOutOfContext | NativeMethods.WineventSkipOwnProcess;

        Marshal.SetLastPInvokeError(0);
        _showHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventObjectShow,
            NativeMethods.EventObjectShow,
            0,
            _showCallback,
            0,
            0,
            flags);

        if (_showHook == 0)
        {
            ThrowHookInstallError(Marshal.GetLastWin32Error(), "EVENT_OBJECT_SHOW");
        }

        Marshal.SetLastPInvokeError(0);
        _nameChangeHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventObjectNameChange,
            NativeMethods.EventObjectNameChange,
            0,
            _nameChangeCallback,
            0,
            0,
            flags);

        if (_nameChangeHook != 0)
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        NativeMethods.UnhookWinEvent(_showHook);
        _showHook = 0;
        ThrowHookInstallError(error, "EVENT_OBJECT_NAMECHANGE");
    }

    private void UninstallHooks()
    {
        // Unhook both independently so one failure cannot leave the other hook untouched.
        var showHook = _showHook;
        var nameChangeHook = _nameChangeHook;
        _showHook = 0;
        _nameChangeHook = 0;
        _started = false;

        if (showHook != 0)
        {
            NativeMethods.UnhookWinEvent(showHook);
        }

        if (nameChangeHook != 0)
        {
            NativeMethods.UnhookWinEvent(nameChangeHook);
        }
    }

    private static void ThrowHookInstallError(int error, string eventName)
    {
        if (error != 0)
        {
            throw new Win32Exception(error, $"SetWinEventHook failed for {eventName}.");
        }

        throw new InvalidOperationException($"SetWinEventHook returned a null hook for {eventName}.");
    }

    private void OnShow(
        nint winEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTimeMilliseconds) =>
        OnNativeEvent(
            WindowEventKind.Shown,
            eventType,
            hwnd,
            idObject,
            idChild,
            eventThread,
            eventTimeMilliseconds);

    private void OnNameChange(
        nint winEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTimeMilliseconds) =>
        OnNativeEvent(
            WindowEventKind.NameChanged,
            eventType,
            hwnd,
            idObject,
            idChild,
            eventThread,
            eventTimeMilliseconds);

    private void OnNativeEvent(
        WindowEventKind kind,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTimeMilliseconds)
    {
        // Native callbacks must stay tiny: validate the window-level event, then enqueue a
        // managed event dispatch. Snapshot collection and rule evaluation happen elsewhere.
        if (Volatile.Read(ref _disposed) != 0 ||
            hwnd == 0 ||
            idObject != NativeMethods.ObjIdWindow ||
            idChild != NativeMethods.ChildIdSelf)
        {
            return;
        }

        var args = new WindowEventArgs(
            hwnd,
            kind,
            eventType,
            eventThread,
            eventTimeMilliseconds);

        ThreadPool.QueueUserWorkItem(
            static state => state.Source.Dispatch(state.Args),
            new DispatchState(this, args),
            preferLocal: false);
    }

    private void Dispatch(WindowEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            WindowEventReceived?.Invoke(this, args);
        }
        catch (Exception exception)
        {
            // Subscriber failures must never escape a native callback or terminate a worker.
            Trace.TraceError("WindowEventReceived subscriber failed: {0}", exception);
        }
    }

    private sealed record DispatchState(WindowEventSource Source, WindowEventArgs Args);
}
