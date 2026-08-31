using System.Runtime.InteropServices;
using System.Text;

namespace StartDown.Interop;

internal static class NativeMethods
{
    internal const uint EventObjectShow = 0x8002;
    internal const uint EventObjectNameChange = 0x800C;

    internal const uint WineventOutOfContext = 0x0000;
    internal const uint WineventSkipOwnProcess = 0x0002;

    internal const int ObjIdWindow = 0;
    internal const int ChildIdSelf = 0;

    internal const uint ProcessQueryLimitedInformation = 0x1000;

    internal const uint GaRoot = 2;
    internal const uint GwOwner = 4;

    internal const uint WmSysCommand = 0x0112;
    internal const nuint ScClose = 0xF060;

    internal const int SwHide = 0;
    internal const int SwMinimize = 6;

    internal const uint DwmwaExtendedFrameBounds = 9;
    internal const uint DwmwaCloaked = 14;

    internal const int ErrorInvalidWindowHandle = 1400;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void WinEventDelegate(
        nint winEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTimeMilliseconds);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal delegate bool EnumWindowsDelegate(nint hwnd, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint eventHookModule,
        WinEventDelegate winEventProc,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint winEventHook);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsDelegate enumFunction, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", ExactSpelling = true, SetLastError = true)]
    internal static extern int GetWindowTextLengthW(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowTextW(nint hwnd, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassNameW(nint hwnd, StringBuilder className, int maximumCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out Rect rectangle);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindowAsync(nint hwnd, int command);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(
        nint process,
        uint flags,
        StringBuilder executableName,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(
        nint hwnd,
        uint attribute,
        out Rect value,
        uint valueSize);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(
        nint hwnd,
        uint attribute,
        out int value,
        uint valueSize);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Rect
    {
        internal readonly int Left;
        internal readonly int Top;
        internal readonly int Right;
        internal readonly int Bottom;

        internal int Width => Math.Max(0, Right - Left);
        internal int Height => Math.Max(0, Bottom - Top);
    }
}
