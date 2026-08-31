using System.Runtime.InteropServices;

namespace StartDown.Interop;

/// <summary>
/// Activates packaged applications through their Application User Model ID.
/// </summary>
public sealed class ApplicationActivationManager
{
    private const int ErrorInvalidParameter = 87;

    public ProcessLaunchResult Activate(string? applicationUserModelId, string? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(applicationUserModelId))
        {
            return Failure(ErrorInvalidParameter, "An Application User Model ID is required.");
        }

        IApplicationActivationManager? activationManager = null;
        try
        {
            activationManager = (IApplicationActivationManager)new NativeApplicationActivationManager();
            var result = activationManager.ActivateApplication(
                applicationUserModelId.Trim(),
                arguments ?? string.Empty,
                ActivateOptions.NoErrorUi,
                out var processId);

            if (result < 0)
            {
                return Failure(result, DescribeHResult(result));
            }

            return new ProcessLaunchResult(
                Succeeded: true,
                ProcessId: processId is > 0 and <= int.MaxValue ? checked((int)processId) : null,
                Win32Error: 0,
                ErrorMessage: null);
        }
        catch (COMException exception)
        {
            return Failure(exception.HResult, exception.Message);
        }
        catch (Exception exception) when (exception is InvalidCastException or InvalidOperationException)
        {
            return Failure(exception.HResult, exception.Message);
        }
        finally
        {
            if (activationManager is not null && Marshal.IsComObject(activationManager))
            {
                Marshal.ReleaseComObject(activationManager);
            }
        }
    }

    private static ProcessLaunchResult Failure(int error, string message) =>
        new(
            Succeeded: false,
            ProcessId: null,
            Win32Error: error,
            ErrorMessage: message);

    private static string DescribeHResult(int hresult)
    {
        return Marshal.GetExceptionForHR(hresult)?.Message
               ?? $"Application activation failed with HRESULT 0x{unchecked((uint)hresult):X8}.";
    }

    [Flags]
    private enum ActivateOptions : uint
    {
        NoErrorUi = 0x00000002,
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class NativeApplicationActivationManager
    {
    }

    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string applicationUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            ActivateOptions options,
            out uint processId);
    }
}
