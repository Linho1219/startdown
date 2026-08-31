using Microsoft.Win32;
using System.Diagnostics;

namespace StartDown.Infrastructure;

internal static class AutostartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "StartDown";

    public static bool IsEnabled(string configurationPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value &&
               string.Equals(value, BuildCommand(configurationPath), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled, string configurationPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(ValueName, BuildCommand(configurationPath), RegistryValueKind.String);
    }

    public static void OpenWindowsStartupSettings()
    {
        Process.Start(new ProcessStartInfo("ms-settings:startupapps")
        {
            UseShellExecute = true
        });
    }

    private static string BuildCommand(string configurationPath)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定 StartDown 的可执行文件路径。");
        var fullConfigurationPath = Path.GetFullPath(configurationPath);
        return $"\"{executable}\" --startup --config \"{fullConfigurationPath}\"";
    }
}
