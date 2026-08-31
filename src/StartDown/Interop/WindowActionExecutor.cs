using System.ComponentModel;
using System.Runtime.InteropServices;
using StartDown.Core;

namespace StartDown.Interop;

public enum WindowActionStatus
{
    Queued,
    InvalidWindow,
    AccessDenied,
    Failed,
}

public sealed record WindowActionResult(
    nint Hwnd,
    WindowAction Action,
    WindowActionStatus Status,
    int Win32Error)
{
    public bool Succeeded => Status == WindowActionStatus.Queued;

    public string? ErrorMessage => Win32Error == 0
        ? null
        : new Win32Exception(Win32Error).Message;
}

public sealed class WindowActionExecutor
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;

    public WindowActionResult Execute(nint hwnd, WindowAction action)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
        {
            return CreateFailure(hwnd, action, NativeMethods.ErrorInvalidWindowHandle);
        }

        return action switch
        {
            WindowAction.Close => QueueClose(hwnd, action),
            WindowAction.Minimize => QueueShowWindow(hwnd, action, NativeMethods.SwMinimize),
            WindowAction.Hide => QueueShowWindow(hwnd, action, NativeMethods.SwHide),
            _ => CreateFailure(hwnd, action, ErrorInvalidParameter),
        };
    }

    private static WindowActionResult QueueClose(nint hwnd, WindowAction action)
    {
        Marshal.SetLastPInvokeError(0);
        var queued = NativeMethods.PostMessageW(
            hwnd,
            NativeMethods.WmSysCommand,
            NativeMethods.ScClose,
            0);
        var error = Marshal.GetLastWin32Error();

        if (queued)
        {
            return new WindowActionResult(hwnd, action, WindowActionStatus.Queued, 0);
        }

        if (error == 0 && !NativeMethods.IsWindow(hwnd))
        {
            error = NativeMethods.ErrorInvalidWindowHandle;
        }

        return CreateFailure(hwnd, action, error);
    }

    private static WindowActionResult QueueShowWindow(nint hwnd, WindowAction action, int command)
    {
        Marshal.SetLastPInvokeError(0);
        var queued = NativeMethods.ShowWindowAsync(hwnd, command);
        var error = Marshal.GetLastWin32Error();

        if (queued)
        {
            return new WindowActionResult(hwnd, action, WindowActionStatus.Queued, 0);
        }

        if (!NativeMethods.IsWindow(hwnd))
        {
            error = NativeMethods.ErrorInvalidWindowHandle;
        }

        return CreateFailure(hwnd, action, error);
    }

    private static WindowActionResult CreateFailure(nint hwnd, WindowAction action, int error) =>
        new(
            hwnd,
            action,
            error switch
            {
                NativeMethods.ErrorInvalidWindowHandle => WindowActionStatus.InvalidWindow,
                ErrorAccessDenied => WindowActionStatus.AccessDenied,
                _ => WindowActionStatus.Failed,
            },
            error);
}
