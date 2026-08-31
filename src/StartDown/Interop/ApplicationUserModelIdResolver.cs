using System.Runtime.InteropServices;
using System.Text;

namespace StartDown.Interop;

/// <summary>
/// Resolves the packaged-application identity associated with a process or window.
/// </summary>
public sealed class ApplicationUserModelIdResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const ushort VtBstr = 8;
    private const ushort VtLpwstr = 31;

    private static readonly Guid IidPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppUserModelIdKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        5);

    /// <summary>
    /// Resolves an AppUserModelID for a window. An explicit window property wins over
    /// process identity. Legacy ApplicationFrameHost windows receive a best-effort
    /// descendant-process fallback.
    /// </summary>
    public string? ResolveWindow(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
        {
            return null;
        }

        if (TryGetWindowProperty(hwnd, out var applicationUserModelId))
        {
            return applicationUserModelId;
        }

        if (NativeMethods.GetWindowThreadProcessId(hwnd, out var processId) == 0 || processId == 0)
        {
            return null;
        }

        if (TryGetForProcess(processId, out applicationUserModelId))
        {
            return applicationUserModelId;
        }

        return IsApplicationFrameHost(processId)
            ? ResolveApplicationFrameDescendant(hwnd, processId)
            : null;
    }

    public bool TryGetForProcess(int processId, out string? applicationUserModelId)
    {
        applicationUserModelId = null;
        return processId > 0 && TryGetForProcess(unchecked((uint)processId), out applicationUserModelId);
    }

    internal bool TryGetForProcess(uint processId, out string? applicationUserModelId)
    {
        applicationUserModelId = null;
        if (processId == 0)
        {
            return false;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return false;
        }

        try
        {
            uint requiredLength = 0;
            var result = GetApplicationUserModelId(process, ref requiredLength, null);
            if (result != ErrorInsufficientBuffer || requiredLength <= 1 || requiredLength > int.MaxValue)
            {
                return false;
            }

            var buffer = new StringBuilder(checked((int)requiredLength));
            result = GetApplicationUserModelId(process, ref requiredLength, buffer);
            if (result != ErrorSuccess || requiredLength <= 1)
            {
                return false;
            }

            var value = buffer.ToString().Trim();
            if (value.Length == 0)
            {
                return false;
            }

            applicationUserModelId = value;
            return true;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool TryGetWindowProperty(nint hwnd, out string? applicationUserModelId)
    {
        applicationUserModelId = null;
        var interfaceId = IidPropertyStore;
        var result = SHGetPropertyStoreForWindow(hwnd, ref interfaceId, out var propertyStore);
        if (result < 0 || propertyStore is null)
        {
            return false;
        }

        try
        {
            var key = AppUserModelIdKey;
            var propertyResult = propertyStore.GetValue(ref key, out var value);
            try
            {
                if (propertyResult < 0)
                {
                    return false;
                }

                var text = value.ValueType switch
                {
                    VtLpwstr when value.PointerValue != 0 => Marshal.PtrToStringUni(value.PointerValue),
                    VtBstr when value.PointerValue != 0 => Marshal.PtrToStringBSTR(value.PointerValue),
                    _ => null,
                };

                text = text?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    return false;
                }

                applicationUserModelId = text;
                return true;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(propertyStore);
        }
    }

    private string? ResolveApplicationFrameDescendant(nint hwnd, uint frameProcessId)
    {
        string? resolved = null;
        var visitedProcessIds = new HashSet<uint>();
        EnumChildWindowsDelegate callback = (child, _) =>
        {
            if (NativeMethods.GetWindowThreadProcessId(child, out var childProcessId) == 0 ||
                childProcessId == 0 ||
                childProcessId == frameProcessId ||
                !visitedProcessIds.Add(childProcessId))
            {
                return true;
            }

            if (!TryGetForProcess(childProcessId, out var candidate))
            {
                return true;
            }

            resolved = candidate;
            return false;
        };

        EnumChildWindows(hwnd, callback, 0);
        GC.KeepAlive(callback);
        return resolved;
    }

    private static bool IsApplicationFrameHost(uint processId)
    {
        return ProcessImagePath.TryGet(processId, out var executablePath, out _) &&
               string.Equals(
                   Path.GetFileName(executablePath),
                   "ApplicationFrameHost.exe",
                   StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        internal readonly Guid FormatId = formatId;
        internal readonly uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        internal ushort ValueType;

        [FieldOffset(8)]
        internal nint PointerValue;

        // The native union also contains two-pointer members. Keeping that maximum-sized
        // member here gives PROPVARIANT its required 24-byte x64 / 16-byte x86 layout.
        [FieldOffset(8)]
        private PointerPair _maximumUnionSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerPair
    {
        private nint _first;
        private nint _second;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumChildWindowsDelegate(nint hwnd, nint parameter);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetPropertyStoreForWindow(
        nint hwnd,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? propertyStore);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        nint parent,
        EnumChildWindowsDelegate callback,
        nint parameter);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(
        nint process,
        ref uint applicationUserModelIdLength,
        StringBuilder? applicationUserModelId);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant propVariant);
}
