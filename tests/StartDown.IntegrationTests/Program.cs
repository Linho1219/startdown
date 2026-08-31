using StartDown.Core;
using StartDown.Interop;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

var repository = FindRepositoryRoot();
#if DEBUG
const string buildConfiguration = "Debug";
#else
const string buildConfiguration = "Release";
#endif
var startDown = Path.Combine(repository, "src", "StartDown", "bin", buildConfiguration, "net10.0-windows", "StartDown.exe");
var fixture = Path.Combine(repository, "tests", "StartDown.WindowFixture", "bin", buildConfiguration, "net10.0-windows", "StartDown.WindowFixture.exe");

Assert(File.Exists(startDown), $"StartDown executable was not found: {startDown}");
Assert(File.Exists(fixture), $"Fixture executable was not found: {fixture}");

var temporaryDirectory = Path.Combine(Path.GetTempPath(), "StartDown.Integration." + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);
var configurationPath = Path.Combine(temporaryDirectory, "config.json");
var startedMarker = Path.Combine(temporaryDirectory, "fixture.started");
var closedMarker = Path.Combine(temporaryDirectory, "fixture.closed");

try
{
    var ordinaryShortcutPath = Path.Combine(temporaryDirectory, "Fixture.lnk");
    CreateShortcut(
        ordinaryShortcutPath,
        fixture,
        "--fixture-argument value",
        Path.GetDirectoryName(fixture)!);
    var shortcut = ShortcutResolver.Resolve(ordinaryShortcutPath);
    Assert(shortcut.TargetKind == ShortcutTargetKind.Executable, "Ordinary shortcut was not classified as executable.");
    Assert(string.Equals(shortcut.ExecutablePath, fixture, StringComparison.OrdinalIgnoreCase), "Ordinary shortcut target path was not preserved.");
    Assert(shortcut.Arguments == "--fixture-argument value", "Ordinary shortcut arguments were not preserved.");
    Assert(string.Equals(shortcut.WorkingDirectory, Path.GetDirectoryName(fixture), StringComparison.OrdinalIgnoreCase), "Ordinary shortcut working directory was not preserved.");
    Console.WriteLine("PASS  Ordinary .lnk import preserves target, arguments, and working directory.");

    var configuration = new AppConfiguration
    {
        GlobalTimeoutSeconds = 15,
        Entries =
        [
            new LaunchEntry
            {
                Name = "Integration fixture",
                ExecutablePath = fixture,
                Arguments = $"--started-marker \"{startedMarker}\" --closed-marker \"{closedMarker}\"",
                TimeoutSeconds = 10,
                ActionDelayMilliseconds = 100,
                WindowRule = new WindowRule
                {
                    TitleMatch = TitleMatchMode.Exact,
                    TitlePattern = "StartDown Integration Fixture",
                    MinWidth = 500,
                    MinHeight = 350,
                },
            },
        ],
    };
    File.WriteAllText(configurationPath, JsonSerializer.Serialize(configuration, new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    }));

    using var runner = Process.Start(new ProcessStartInfo(startDown)
    {
        UseShellExecute = false,
        ArgumentList = { "--run", "--config", configurationPath },
    }) ?? throw new InvalidOperationException("Could not start StartDown.");

    WaitForExitOrKill(runner, 20_000, "StartDown runner");
    Assert(runner.ExitCode == 0, $"StartDown returned exit code {runner.ExitCode}.");
    Assert(File.Exists(startedMarker), "The fixture never started; a runner mutex conflict may have been misreported as success.");

    var fixtureClosed = WaitUntil(
        () => File.Exists(closedMarker),
        TimeSpan.FromSeconds(5));
    Assert(fixtureClosed, "The fixture did not process the close request after StartDown completed.");
    Console.WriteLine("PASS  StartDown launched the fixture, matched its window, closed it, and exited.");

    var missingPackagedApp = new AppConfiguration
    {
        GlobalTimeoutSeconds = 5,
        Entries =
        [
            new LaunchEntry
            {
                Name = "Missing packaged app",
                LaunchKind = LaunchKind.ApplicationUserModelId,
                ApplicationUserModelId = "StartDown.NonexistentPackage_0000000000000!App",
                TimeoutSeconds = 2,
            },
        ],
    };
    File.WriteAllText(configurationPath, JsonSerializer.Serialize(missingPackagedApp, new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    }));
    using var missingPackagedAppRun = Process.Start(new ProcessStartInfo(startDown)
    {
        UseShellExecute = false,
        ArgumentList = { "--startup", "--config", configurationPath },
    }) ?? throw new InvalidOperationException("Could not start the packaged-app activation check.");
    WaitForExitOrKill(missingPackagedAppRun, 10_000, "Missing packaged-app check");
    Assert(missingPackagedAppRun.ExitCode == 1, $"Missing packaged app returned {missingPackagedAppRun.ExitCode}, expected 1.");
    Console.WriteLine("PASS  Missing packaged app activation fails quietly without hanging the runner.");

    File.WriteAllText(configurationPath, "{ this is not valid json");
    using var invalidConfigurationRun = Process.Start(new ProcessStartInfo(startDown)
    {
        UseShellExecute = false,
        ArgumentList = { "--startup", "--config", configurationPath },
    }) ?? throw new InvalidOperationException("Could not start the invalid-configuration check.");
    WaitForExitOrKill(invalidConfigurationRun, 10_000, "Invalid-configuration check");
    Assert(invalidConfigurationRun.ExitCode == 2, $"Invalid configuration returned {invalidConfigurationRun.ExitCode}, expected 2.");
    Assert(File.ReadAllText(configurationPath) == "{ this is not valid json", "StartDown modified the corrupt configuration file.");
    Console.WriteLine("PASS  Corrupt configuration fails closed and remains unchanged.");

    var missingConfigurationPath = Path.Combine(temporaryDirectory, "missing.json");
    using var missingConfigurationRun = Process.Start(new ProcessStartInfo(startDown)
    {
        UseShellExecute = false,
        ArgumentList = { "--startup", "--config", missingConfigurationPath },
    }) ?? throw new InvalidOperationException("Could not start the missing-configuration check.");
    WaitForExitOrKill(missingConfigurationRun, 10_000, "Missing-configuration check");
    Assert(missingConfigurationRun.ExitCode == 2, $"Missing configuration returned {missingConfigurationRun.ExitCode}, expected 2.");
    Console.WriteLine("PASS  Startup mode requires its configuration file to exist.");

    File.WriteAllText(configurationPath, "{\"SchemaVersion\":1,\"GlobalTimeoutSeconds\":15,\"Entries\":null}");
    using var nullEntriesRun = Process.Start(new ProcessStartInfo(startDown)
    {
        UseShellExecute = false,
        ArgumentList = { "--startup", "--config", configurationPath },
    }) ?? throw new InvalidOperationException("Could not start the null-entries check.");
    WaitForExitOrKill(nullEntriesRun, 10_000, "Null-entries check");
    Assert(nullEntriesRun.ExitCode == 2, $"Null entries returned {nullEntriesRun.ExitCode}, expected 2.");
    Console.WriteLine("PASS  Structurally null configuration cannot normalize into success.");

    using var invalidCommandLineRun = Process.Start(new ProcessStartInfo(startDown)
    {
        UseShellExecute = false,
        ArgumentList = { "--startup", "--entry", "not-a-guid" },
    }) ?? throw new InvalidOperationException("Could not start the invalid-command-line check.");
    WaitForExitOrKill(invalidCommandLineRun, 10_000, "Invalid-command-line check");
    Assert(invalidCommandLineRun.ExitCode == 64, $"Invalid command line returned {invalidCommandLineRun.ExitCode}, expected 64.");
    Console.WriteLine("PASS  Invalid entry id cannot fall back to running every entry.");

    using var missingConfigValueRun = Process.Start(new ProcessStartInfo(startDown)
    {
        UseShellExecute = false,
        ArgumentList = { "--startup", "--config", "--show-status" },
    }) ?? throw new InvalidOperationException("Could not start the missing-config-value check.");
    WaitForExitOrKill(missingConfigValueRun, 10_000, "Missing config value check");
    Assert(missingConfigValueRun.ExitCode == 64, $"Option-like config value returned {missingConfigValueRun.ExitCode}, expected 64.");
    Console.WriteLine("PASS  --config cannot consume the next option as a path.");
}
finally
{
    if (File.Exists(startedMarker) && int.TryParse(File.ReadAllText(startedMarker), out var fixtureProcessId))
    {
        try
        {
            using var process = Process.GetProcessById(fixtureProcessId);
            if (string.Equals(process.ProcessName, "StartDown.WindowFixture", StringComparison.OrdinalIgnoreCase))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch { }
    }

    try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StartDown.slnx")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
}

static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
{
    var stopwatch = Stopwatch.StartNew();
    while (stopwatch.Elapsed < timeout)
    {
        if (condition())
        {
            return true;
        }
        Thread.Sleep(50);
    }
    return condition();
}

static void WaitForExitOrKill(Process process, int timeoutMilliseconds, string description)
{
    if (process.WaitForExit(timeoutMilliseconds))
    {
        return;
    }

    try
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5_000);
    }
    catch
    {
    }

    throw new InvalidOperationException($"{description} did not exit before the integration timeout.");
}

static void CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDirectory)
{
    object? shell = null;
    object? shortcut = null;
    try
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
        shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not create WScript.Shell.");
        shortcut = shell.GetType().InvokeMember(
            "CreateShortcut",
            BindingFlags.InvokeMethod,
            binder: null,
            shell,
            [shortcutPath],
            CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("Could not create the test shortcut.");
        SetComProperty(shortcut, "TargetPath", targetPath);
        SetComProperty(shortcut, "Arguments", arguments);
        SetComProperty(shortcut, "WorkingDirectory", workingDirectory);
        shortcut.GetType().InvokeMember(
            "Save",
            BindingFlags.InvokeMethod,
            binder: null,
            shortcut,
            args: null,
            CultureInfo.InvariantCulture);
    }
    finally
    {
        if (shortcut is not null && Marshal.IsComObject(shortcut))
        {
            Marshal.FinalReleaseComObject(shortcut);
        }
        if (shell is not null && Marshal.IsComObject(shell))
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}

static void SetComProperty(object target, string name, object value) =>
    target.GetType().InvokeMember(
        name,
        BindingFlags.SetProperty,
        binder: null,
        target,
        [value],
        CultureInfo.InvariantCulture);

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
