using System.ComponentModel;
using System.Diagnostics;
using StartDown.Core;

namespace StartDown.Interop;

public sealed record ProcessLaunchResult(
    bool Succeeded,
    int? ProcessId,
    int Win32Error,
    string? ErrorMessage);

public sealed class ProcessLauncher
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;

    private readonly ApplicationActivationManager _applicationActivationManager = new();
    private readonly ApplicationUserModelIdResolver _applicationUserModelIdResolver = new();

    public ProcessLaunchResult Launch(LaunchEntry entry)
    {
        if (entry is null)
        {
            return Failure(ErrorInvalidParameter, "A launch entry is required.");
        }

        return entry.LaunchKind switch
        {
            LaunchKind.Executable => Launch(
                entry.ExecutablePath,
                entry.Arguments,
                entry.WorkingDirectory),
            LaunchKind.ApplicationUserModelId => _applicationActivationManager.Activate(
                entry.ApplicationUserModelId,
                entry.Arguments),
            _ => Failure(ErrorInvalidParameter, "The launch kind is not supported."),
        };
    }

    public ProcessLaunchResult Launch(
        string executablePath,
        string? arguments = null,
        string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return Failure(ErrorInvalidParameter, "An executable path is required.");
        }

        string fullExecutablePath;
        try
        {
            fullExecutablePath = Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(ErrorInvalidParameter, exception.Message);
        }

        string resolvedWorkingDirectory;
        try
        {
            resolvedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Path.GetDirectoryName(fullExecutablePath) ?? Environment.CurrentDirectory
                : Path.GetFullPath(workingDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(ErrorInvalidParameter, exception.Message);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fullExecutablePath,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = resolvedWorkingDirectory,
            UseShellExecute = false,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return Failure(0, "Process.Start did not return a process.");
            }

            return new ProcessLaunchResult(
                Succeeded: true,
                ProcessId: process.Id,
                Win32Error: 0,
                ErrorMessage: null);
        }
        catch (Win32Exception exception)
        {
            return Failure(exception.NativeErrorCode, exception.Message);
        }
        catch (FileNotFoundException exception)
        {
            return Failure(ErrorFileNotFound, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(ErrorAccessDenied, exception.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException or NotSupportedException)
        {
            return Failure(0, exception.Message);
        }
    }

    public bool IsRunningExactPath(string executablePath) =>
        FindRunningExactPath(executablePath).Count != 0;

    public IReadOnlyList<int> FindRunning(LaunchEntry entry)
    {
        if (entry is null)
        {
            return Array.Empty<int>();
        }

        return entry.LaunchKind switch
        {
            LaunchKind.Executable => FindRunningExactPath(entry.ExecutablePath),
            LaunchKind.ApplicationUserModelId => FindRunningApplicationUserModelId(
                entry.ApplicationUserModelId),
            _ => Array.Empty<int>(),
        };
    }

    public IReadOnlyList<int> FindRunningApplicationUserModelId(string? applicationUserModelId)
    {
        if (string.IsNullOrWhiteSpace(applicationUserModelId))
        {
            return Array.Empty<int>();
        }

        var expected = applicationUserModelId.Trim();
        var processIds = new List<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (_applicationUserModelIdResolver.TryGetForProcess(
                            process.Id,
                            out var actualApplicationUserModelId) &&
                        string.Equals(
                            expected,
                            actualApplicationUserModelId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        processIds.Add(process.Id);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited between enumeration and identity inspection.
                }
            }
        }

        return processIds.AsReadOnly();
    }

    public IReadOnlyList<int> FindRunningExactPath(string executablePath)
    {
        if (!TryNormalizePath(executablePath, out var expectedPath))
        {
            return Array.Empty<int>();
        }

        var processIds = new List<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (ProcessImagePath.TryGet(process.Id, out var actualPath, out _) &&
                        TryNormalizePath(actualPath, out var normalizedActualPath) &&
                        string.Equals(expectedPath, normalizedActualPath, StringComparison.OrdinalIgnoreCase))
                    {
                        processIds.Add(process.Id);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited between enumeration and path inspection.
                }
            }
        }

        return processIds.AsReadOnly();
    }

    private static ProcessLaunchResult Failure(int error, string message) =>
        new(
            Succeeded: false,
            ProcessId: null,
            Win32Error: error,
            ErrorMessage: message);

    private static bool TryNormalizePath(string? path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var candidate = path;
            if (candidate.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                candidate = @"\\" + candidate[8..];
            }
            else if (candidate.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate[4..];
            }

            normalizedPath = Path.GetFullPath(candidate);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
