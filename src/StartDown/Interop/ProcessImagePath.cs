using System.Runtime.InteropServices;
using System.Text;

namespace StartDown.Interop;

public static class ProcessImagePath
{
    private const int MaximumWindowsPathLength = 32_768;

    public static bool TryGet(int processId, out string? executablePath, out int win32Error)
    {
        if (processId <= 0)
        {
            executablePath = null;
            win32Error = 87; // ERROR_INVALID_PARAMETER
            return false;
        }

        return TryGet(unchecked((uint)processId), out executablePath, out win32Error);
    }

    internal static bool TryGet(uint processId, out string? executablePath, out int win32Error)
    {
        executablePath = null;

        Marshal.SetLastPInvokeError(0);
        var process = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);

        if (process == 0)
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            var buffer = new StringBuilder(MaximumWindowsPathLength);
            var size = unchecked((uint)buffer.Capacity);

            Marshal.SetLastPInvokeError(0);
            if (!NativeMethods.QueryFullProcessImageNameW(process, 0, buffer, ref size))
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }

            executablePath = buffer.ToString(0, checked((int)size));
            win32Error = 0;
            return true;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }
}
