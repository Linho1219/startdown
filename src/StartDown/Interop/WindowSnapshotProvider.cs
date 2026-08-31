using System.Runtime.InteropServices;
using System.Text;
using StartDown.Core;

namespace StartDown.Interop;

public sealed class WindowSnapshotProvider
{
    private const int MaximumWindowTextLength = 32_768;
    private const int ClassNameCapacity = 512;

    private readonly ApplicationUserModelIdResolver _applicationUserModelIdResolver = new();

    public WindowSnapshot? Capture(nint hwnd) =>
        TryCapture(hwnd, out var snapshot) ? snapshot : null;

    public bool TryCapture(nint hwnd, out WindowSnapshot? snapshot)
    {
        snapshot = null;

        if (hwnd == 0)
        {
            return false;
        }

        // WinEvent can report the child CoreWindow hosted inside an old UWP frame.
        // Rules and actions must use the user-visible root window instead.
        var rootWindow = NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot);
        if (rootWindow != 0)
        {
            hwnd = rootWindow;
        }

        if (!NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        var windowThread = NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (windowThread == 0 || processId == 0)
        {
            return false;
        }

        ProcessImagePath.TryGet(processId, out var executablePath, out _);
        var applicationUserModelId = _applicationUserModelIdResolver.ResolveWindow(hwnd);

        var title = ReadWindowText(hwnd);
        var className = ReadClassName(hwnd);
        var bounds = ReadBounds(hwnd);
        var isVisible = NativeMethods.IsWindowVisible(hwnd);
        var isMinimized = NativeMethods.IsIconic(hwnd);
        var isTopLevel = NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot) == hwnd;
        var isOwned = NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) != 0;
        var isCloaked = ReadIsCloaked(hwnd);

        // The handle may have been destroyed and reused while its properties were being read.
        // Only publish a coherent snapshot if it still resolves to the same process.
        if (!NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var verifiedProcessId);
        if (verifiedProcessId != processId)
        {
            return false;
        }

        snapshot = new WindowSnapshot(
            Hwnd: hwnd,
            ProcessId: unchecked((int)processId),
            ExecutablePath: executablePath,
            Title: title,
            ClassName: className,
            Width: bounds.Width,
            Height: bounds.Height,
            IsVisible: isVisible,
            IsTopLevel: isTopLevel,
            IsOwned: isOwned,
            IsCloaked: isCloaked,
            IsMinimized: isMinimized,
            ApplicationUserModelId: applicationUserModelId);

        return true;
    }

    private static string ReadWindowText(nint hwnd)
    {
        var reportedLength = NativeMethods.GetWindowTextLengthW(hwnd);
        var capacity = Math.Clamp(reportedLength + 1, 256, MaximumWindowTextLength);
        var buffer = new StringBuilder(capacity);
        var copied = NativeMethods.GetWindowTextW(hwnd, buffer, buffer.Capacity);

        // The caption can grow between GetWindowTextLength and GetWindowText. Retry once at
        // the Windows maximum when the first result filled the available buffer.
        if (copied == buffer.Capacity - 1 && buffer.Capacity < MaximumWindowTextLength)
        {
            buffer = new StringBuilder(MaximumWindowTextLength);
            NativeMethods.GetWindowTextW(hwnd, buffer, buffer.Capacity);
        }

        return buffer.ToString();
    }

    private static string ReadClassName(nint hwnd)
    {
        var buffer = new StringBuilder(ClassNameCapacity);
        NativeMethods.GetClassNameW(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static NativeMethods.Rect ReadBounds(nint hwnd)
    {
        var rectSize = unchecked((uint)Marshal.SizeOf<NativeMethods.Rect>());
        var result = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaExtendedFrameBounds,
            out NativeMethods.Rect rectangle,
            rectSize);

        if (result == 0)
        {
            return rectangle;
        }

        return NativeMethods.GetWindowRect(hwnd, out rectangle)
            ? rectangle
            : default;
    }

    private static bool ReadIsCloaked(nint hwnd)
    {
        var valueSize = unchecked((uint)sizeof(int));
        var result = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaCloaked,
            out int cloaked,
            valueSize);

        return result == 0 && cloaked != 0;
    }
}
