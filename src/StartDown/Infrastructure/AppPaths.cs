namespace StartDown.Infrastructure;

internal static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StartDown");

    public static string ConfigurationFile { get; } = Path.Combine(DataDirectory, "config.json");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");
}
