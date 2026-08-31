namespace StartDown.Core;

/// <summary>
/// The persisted StartDown configuration.
/// </summary>
public sealed class AppConfiguration
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultGlobalTimeoutSeconds = 300;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public int GlobalTimeoutSeconds { get; set; } = DefaultGlobalTimeoutSeconds;

    public List<LaunchEntry> Entries { get; set; } = [];
}

/// <summary>
/// Describes an application StartDown launches and the window it should handle.
/// </summary>
public sealed class LaunchEntry
{
    public const int DefaultTimeoutSeconds = 60;
    public const int DefaultExpectedMatches = 1;
    public const int DefaultActionDelayMilliseconds = 250;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public LaunchKind LaunchKind { get; set; } = LaunchKind.Executable;

    public string ExecutablePath { get; set; } = string.Empty;

    public string? ApplicationUserModelId { get; set; }

    public string? Arguments { get; set; }

    public string? WorkingDirectory { get; set; }

    public ProcessMatchScope ProcessMatchScope { get; set; } = ProcessMatchScope.ExactLaunchPath;

    public ExistingInstancePolicy ExistingInstancePolicy { get; set; } = ExistingInstancePolicy.Skip;

    /// <summary>
    /// The exact executable or containing directory used by scopes other than
    /// <see cref="ProcessMatchScope.ExactLaunchPath"/>.
    /// </summary>
    public string? MatchPath { get; set; }

    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    public int ExpectedMatches { get; set; } = DefaultExpectedMatches;

    public int ActionDelayMilliseconds { get; set; } = DefaultActionDelayMilliseconds;

    public WindowRule WindowRule { get; set; } = new();

    public WindowAction Action { get; set; } = WindowAction.Close;
}

public enum LaunchKind
{
    /// <summary>Launch a traditional executable path.</summary>
    Executable,

    /// <summary>Activate a packaged Windows application by its AUMID.</summary>
    ApplicationUserModelId,
}

public enum ProcessMatchScope
{
    /// <summary>Match the executable path or AUMID passed to the launcher.</summary>
    ExactLaunchPath,

    /// <summary>Match an explicit executable path from <see cref="LaunchEntry.MatchPath"/>.</summary>
    ExactPath,

    /// <summary>Match executables beneath the directory in <see cref="LaunchEntry.MatchPath"/>.</summary>
    Directory,
}

/// <summary>
/// Controls what the runner does when the launch executable is already running.
/// </summary>
public enum ExistingInstancePolicy
{
    /// <summary>Do not launch or monitor this entry.</summary>
    Skip,

    /// <summary>Do not launch again, but monitor existing and future windows.</summary>
    Adopt,
}

public sealed class WindowRule
{
    public TitleMatchMode TitleMatch { get; set; } = TitleMatchMode.Any;

    public string? TitlePattern { get; set; }

    /// <summary>An optional exact window class name.</summary>
    public string? ClassName { get; set; }

    public int? MinWidth { get; set; }

    public int? MaxWidth { get; set; }

    public int? MinHeight { get; set; }

    public int? MaxHeight { get; set; }

    public bool RequireVisible { get; set; } = true;

    public bool RequireTopLevel { get; set; } = true;

    public bool RequireUnowned { get; set; } = true;

    public bool RequireNotMinimized { get; set; } = true;
}

public enum TitleMatchMode
{
    Any,
    Contains,
    Exact,
    Regex,
}

public enum WindowAction
{
    Close,
    Minimize,
    Hide,
}

/// <summary>
/// Immutable facts captured for a native window at a point in time.
/// </summary>
public sealed record WindowSnapshot(
    nint Hwnd,
    int ProcessId,
    string? ExecutablePath,
    string Title,
    string ClassName,
    int Width,
    int Height,
    bool IsVisible,
    bool IsTopLevel,
    bool IsOwned,
    bool IsCloaked,
    bool IsMinimized,
    string? ApplicationUserModelId = null);
