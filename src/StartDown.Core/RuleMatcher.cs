using System.Text.RegularExpressions;

namespace StartDown.Core;

/// <summary>
/// Pure matching logic shared by the configuration UI and the runtime monitor.
/// </summary>
public static class RuleMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Returns true only when both the process scope and every enabled window
    /// condition match the snapshot.
    /// </summary>
    public static bool Matches(LaunchEntry? entry, WindowSnapshot? snapshot)
    {
        return entry is not null
            && snapshot is not null
            && MatchesProcess(entry, snapshot.ExecutablePath)
            && MatchesWindow(entry.WindowRule, snapshot);
    }

    public static bool MatchesProcess(LaunchEntry? entry, string? executablePath)
    {
        if (entry is null || !TryNormalizeWindowsPath(executablePath, out var candidatePath))
        {
            return false;
        }

        return entry.ProcessMatchScope switch
        {
            ProcessMatchScope.ExactLaunchPath => PathsEqual(entry.ExecutablePath, candidatePath),
            ProcessMatchScope.ExactPath => PathsEqual(entry.MatchPath, candidatePath),
            ProcessMatchScope.Directory => IsPathInsideDirectory(entry.MatchPath, candidatePath),
            _ => false,
        };
    }

    public static bool MatchesWindow(WindowRule? rule, WindowSnapshot? snapshot)
    {
        if (rule is null || snapshot is null)
        {
            return false;
        }

        // DWM-cloaked windows can still carry WS_VISIBLE even though the user
        // cannot see them, so "visible" intentionally excludes both states.
        if (rule.RequireVisible && (!snapshot.IsVisible || snapshot.IsCloaked))
        {
            return false;
        }

        if (rule.RequireTopLevel && !snapshot.IsTopLevel)
        {
            return false;
        }

        if (rule.RequireUnowned && snapshot.IsOwned)
        {
            return false;
        }

        if (rule.RequireNotMinimized && snapshot.IsMinimized)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.ClassName)
            && !string.Equals(rule.ClassName, snapshot.ClassName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!MatchesTitle(rule, snapshot.Title))
        {
            return false;
        }

        return IsAtLeast(snapshot.Width, rule.MinWidth)
            && IsAtMost(snapshot.Width, rule.MaxWidth)
            && IsAtLeast(snapshot.Height, rule.MinHeight)
            && IsAtMost(snapshot.Height, rule.MaxHeight);
    }

    internal static bool TryNormalizeWindowsPath(string? path, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var value = path.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1].Trim();
        }

        value = Environment.ExpandEnvironmentVariables(value);

        try
        {
            if (!Path.IsPathFullyQualified(value))
            {
                return false;
            }

            normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            return normalizedPath.Length > 0;
        }
        catch (Exception exception) when (exception is ArgumentException
                                             or NotSupportedException
                                             or PathTooLongException)
        {
            return false;
        }
    }

    private static bool MatchesTitle(WindowRule rule, string? title)
    {
        title ??= string.Empty;
        var pattern = rule.TitlePattern;

        return rule.TitleMatch switch
        {
            TitleMatchMode.Any => true,
            TitleMatchMode.Contains => !string.IsNullOrEmpty(pattern)
                && title.Contains(pattern, StringComparison.OrdinalIgnoreCase),
            TitleMatchMode.Exact => pattern is not null
                && string.Equals(title, pattern, StringComparison.OrdinalIgnoreCase),
            TitleMatchMode.Regex => MatchesRegex(title, pattern),
            _ => false,
        };
    }

    private static bool MatchesRegex(string title, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        try
        {
            return Regex.IsMatch(title, pattern, RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string? configuredPath, string normalizedCandidate)
    {
        return TryNormalizeWindowsPath(configuredPath, out var normalizedConfiguredPath)
            && string.Equals(
                normalizedConfiguredPath,
                normalizedCandidate,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathInsideDirectory(string? configuredDirectory, string normalizedCandidate)
    {
        if (!TryNormalizeWindowsPath(configuredDirectory, out var normalizedDirectory))
        {
            return false;
        }

        var directoryWithBoundary = Path.EndsInDirectorySeparator(normalizedDirectory)
            ? normalizedDirectory
            : normalizedDirectory + Path.DirectorySeparatorChar;

        return normalizedCandidate.StartsWith(directoryWithBoundary, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAtLeast(int value, int? minimum) => minimum is null || value >= minimum.Value;

    private static bool IsAtMost(int value, int? maximum) => maximum is null || value <= maximum.Value;
}
