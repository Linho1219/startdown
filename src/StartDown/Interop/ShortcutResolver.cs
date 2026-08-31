using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace StartDown.Interop;

/// <summary>
/// Identifies the kind of target stored in a Windows Shell shortcut.
/// </summary>
public enum ShortcutTargetKind
{
    Executable,
    AppUserModelId,
}

/// <summary>
/// The launch information extracted from a Windows Shell shortcut.
/// </summary>
public sealed record ShortcutResolution(
    string ShortcutPath,
    ShortcutTargetKind TargetKind,
    string Target,
    string? Arguments,
    string? WorkingDirectory,
    string? Description)
{
    public string? ExecutablePath =>
        TargetKind == ShortcutTargetKind.Executable ? Target : null;

    public string? AppUserModelId =>
        TargetKind == ShortcutTargetKind.AppUserModelId ? Target : null;
}

/// <summary>
/// Reads ordinary executable shortcuts and packaged-application shortcuts
/// without activating their targets.
/// </summary>
public static partial class ShortcutResolver
{
    private const string WshShellProgId = "WScript.Shell";
    private const string ShellApplicationProgId = "Shell.Application";
    private const string TargetParsingPathProperty = "System.Link.TargetParsingPath";
    private const string AppsFolderPrefix = @"shell:AppsFolder\";

    [GeneratedRegex(@"^[^!\\/:\s]+![^!\\/:\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AppUserModelIdPattern();

    public static ShortcutResolution Resolve(string shortcutPath)
    {
        var fullShortcutPath = ValidateShortcutPath(shortcutPath);
        var shellLink = ReadShellLink(fullShortcutPath);

        if (!string.IsNullOrWhiteSpace(shellLink.TargetPath))
        {
            var executablePath = ValidateExecutableTarget(shellLink.TargetPath);
            return new ShortcutResolution(
                fullShortcutPath,
                ShortcutTargetKind.Executable,
                executablePath,
                shellLink.Arguments,
                shellLink.WorkingDirectory,
                shellLink.Description);
        }

        var targetParsingPath = NormalizeTargetParsingPath(ReadTargetParsingPath(fullShortcutPath));
        if (string.IsNullOrWhiteSpace(targetParsingPath) ||
            !AppUserModelIdPattern().IsMatch(targetParsingPath))
        {
            throw new InvalidDataException(
                "The shortcut does not resolve to an executable or a packaged application ID.");
        }

        return new ShortcutResolution(
            fullShortcutPath,
            ShortcutTargetKind.AppUserModelId,
            targetParsingPath,
            shellLink.Arguments,
            shellLink.WorkingDirectory,
            shellLink.Description);
    }

    private static string ValidateShortcutPath(string shortcutPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(shortcutPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The shortcut path is invalid.", nameof(shortcutPath), exception);
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The selected file is not a .lnk shortcut.", nameof(shortcutPath));
        }

        if (Directory.Exists(fullPath))
        {
            throw new ArgumentException("The shortcut path refers to a directory.", nameof(shortcutPath));
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The shortcut file does not exist.", fullPath);
        }

        return fullPath;
    }

    private static string ValidateExecutableTarget(string targetPath)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(targetPath.Trim());
        if (LooksLikeUrlOrShellTarget(expandedPath))
        {
            throw new InvalidDataException("URL and Shell namespace shortcut targets are not supported.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(expandedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("The shortcut target path is invalid.", exception);
        }

        if (Directory.Exists(fullPath))
        {
            throw new InvalidDataException("Directory shortcut targets are not supported.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The shortcut target does not exist.", fullPath);
        }

        var extension = Path.GetExtension(fullPath);
        if (!string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The shortcut target is not a directly executable .exe or .com file.");
        }

        return fullPath;
    }

    private static bool LooksLikeUrlOrShellTarget(string target)
    {
        if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile;
    }

    private static string? NormalizeTargetParsingPath(string? targetParsingPath)
    {
        var value = targetParsingPath?.Trim();
        if (value?.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase) == true)
        {
            value = value[AppsFolderPrefix.Length..];
        }
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static ShellLinkData ReadShellLink(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = CreateComObject(WshShellProgId);
            shortcut = InvokeMethod(shell, "CreateShortcut", shortcutPath)
                ?? throw new InvalidDataException("Windows Script Host could not open the shortcut.");

            return new ShellLinkData(
                OptionalString(GetProperty(shortcut, "TargetPath")),
                OptionalString(GetProperty(shortcut, "Arguments")),
                OptionalString(GetProperty(shortcut, "WorkingDirectory")),
                OptionalString(GetProperty(shortcut, "Description")));
        }
        catch (Exception exception) when (IsComInvocationFailure(exception))
        {
            throw new InvalidDataException("Windows could not read the shortcut.", exception);
        }
        finally
        {
            FinalReleaseComObject(shortcut);
            FinalReleaseComObject(shell);
        }
    }

    private static string? ReadTargetParsingPath(string shortcutPath)
    {
        object? shell = null;
        object? folder = null;
        object? item = null;
        try
        {
            var directoryPath = Path.GetDirectoryName(shortcutPath)
                ?? throw new InvalidDataException("The shortcut has no containing directory.");
            var fileName = Path.GetFileName(shortcutPath);

            shell = CreateComObject(ShellApplicationProgId);
            folder = InvokeMethod(shell, "NameSpace", directoryPath)
                ?? throw new InvalidDataException("Windows Shell could not open the shortcut directory.");
            item = InvokeMethod(folder, "ParseName", fileName)
                ?? throw new InvalidDataException("Windows Shell could not find the shortcut item.");

            return OptionalString(InvokeMethod(item, "ExtendedProperty", TargetParsingPathProperty));
        }
        catch (Exception exception) when (IsComInvocationFailure(exception))
        {
            throw new InvalidDataException("Windows Shell could not resolve the shortcut target.", exception);
        }
        finally
        {
            FinalReleaseComObject(item);
            FinalReleaseComObject(folder);
            FinalReleaseComObject(shell);
        }
    }

    private static object CreateComObject(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: false)
            ?? throw new PlatformNotSupportedException($"The Windows COM component '{progId}' is unavailable.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"The Windows COM component '{progId}' could not be created.");
    }

    private static object? GetProperty(object target, string memberName) =>
        target.GetType().InvokeMember(
            memberName,
            BindingFlags.GetProperty,
            binder: null,
            target,
            args: null,
            CultureInfo.InvariantCulture);

    private static object? InvokeMethod(object target, string memberName, params object?[] arguments) =>
        target.GetType().InvokeMember(
            memberName,
            BindingFlags.InvokeMethod,
            binder: null,
            target,
            arguments,
            CultureInfo.InvariantCulture);

    private static string? OptionalString(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool IsComInvocationFailure(Exception exception) =>
        exception is COMException or InvalidCastException or MissingMethodException or TargetInvocationException;

    private static void FinalReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private sealed record ShellLinkData(
        string? TargetPath,
        string? Arguments,
        string? WorkingDirectory,
        string? Description);
}
