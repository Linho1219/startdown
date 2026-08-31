using System.Reflection;

namespace StartDown.UI;

internal static class ApplicationIconProvider
{
    private static readonly Lazy<Icon?> ApplicationIcon = new(LoadApplicationIcon);

    public static Icon? CreateIcon() =>
        ApplicationIcon.Value is { } icon ? (Icon)icon.Clone() : null;

    private static Icon? LoadApplicationIcon()
    {
        try
        {
            var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
            var appHostPath = string.IsNullOrWhiteSpace(assemblyName)
                ? null
                : Path.Combine(AppContext.BaseDirectory, assemblyName + ".exe");
            var executablePath = appHostPath is not null && File.Exists(appHostPath)
                ? appHostPath
                : Application.ExecutablePath;
            return string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)
                ? null
                : Icon.ExtractAssociatedIcon(executablePath);
        }
        catch
        {
            // The embedded apphost icon remains available to Explorer even if a particular
            // host cannot expose it as a managed Icon for a window title bar.
            return null;
        }
    }
}
