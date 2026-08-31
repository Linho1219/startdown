using System.Text.RegularExpressions;

namespace StartDown.Core;

public sealed record ConfigurationValidationIssue(string Path, string Message);

public sealed class ConfigurationValidationResult
{
    internal ConfigurationValidationResult(
        AppConfiguration configuration,
        IReadOnlyList<ConfigurationValidationIssue> issues)
    {
        Configuration = configuration;
        Issues = issues;
    }

    public AppConfiguration Configuration { get; }

    public IReadOnlyList<ConfigurationValidationIssue> Issues { get; }

    public bool IsValid => Issues.Count == 0;
}

/// <summary>
/// Produces a detached, canonical configuration without touching the file system.
/// </summary>
public static class ConfigurationNormalizer
{
    public static AppConfiguration Normalize(AppConfiguration? configuration)
    {
        configuration ??= new AppConfiguration();

        var normalized = new AppConfiguration
        {
            SchemaVersion = configuration.SchemaVersion,
            GlobalTimeoutSeconds = configuration.GlobalTimeoutSeconds,
            Entries = [],
        };

        if (configuration.Entries is null)
        {
            return normalized;
        }

        foreach (var entry in configuration.Entries)
        {
            if (entry is not null)
            {
                normalized.Entries.Add(NormalizeEntry(entry));
            }
        }

        return normalized;
    }

    private static LaunchEntry NormalizeEntry(LaunchEntry entry)
    {
        var normalizedExecutablePath = NormalizePath(entry.ExecutablePath) ?? string.Empty;

        return new LaunchEntry
        {
            Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
            Name = entry.Name?.Trim() ?? string.Empty,
            Enabled = entry.Enabled,
            LaunchKind = entry.LaunchKind,
            ExecutablePath = normalizedExecutablePath,
            ApplicationUserModelId = NormalizeApplicationUserModelId(entry.ApplicationUserModelId),
            Arguments = NullIfWhiteSpace(entry.Arguments),
            WorkingDirectory = NormalizePath(entry.WorkingDirectory),
            ProcessMatchScope = entry.ProcessMatchScope,
            ExistingInstancePolicy = entry.ExistingInstancePolicy,
            MatchPath = NormalizePath(entry.MatchPath),
            TimeoutSeconds = entry.TimeoutSeconds,
            ExpectedMatches = entry.ExpectedMatches,
            ActionDelayMilliseconds = entry.ActionDelayMilliseconds,
            WindowRule = NormalizeRule(entry.WindowRule),
            Action = entry.Action,
        };
    }

    private static WindowRule NormalizeRule(WindowRule? rule)
    {
        rule ??= new WindowRule();

        return new WindowRule
        {
            TitleMatch = rule.TitleMatch,
            TitlePattern = rule.TitleMatch == TitleMatchMode.Any
                ? null
                : NullIfWhiteSpace(rule.TitlePattern),
            ClassName = NullIfWhiteSpace(rule.ClassName)?.Trim(),
            MinWidth = rule.MinWidth,
            MaxWidth = rule.MaxWidth,
            MinHeight = rule.MinHeight,
            MaxHeight = rule.MaxHeight,
            RequireVisible = rule.RequireVisible,
            RequireTopLevel = rule.RequireTopLevel,
            RequireUnowned = rule.RequireUnowned,
            RequireNotMinimized = rule.RequireNotMinimized,
        };
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (RuleMatcher.TryNormalizeWindowsPath(path, out var normalizedPath))
        {
            return normalizedPath;
        }

        var value = path.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1].Trim();
        }

        return Environment.ExpandEnvironmentVariables(value);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? NormalizeApplicationUserModelId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class ConfigurationValidator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Normalizes a detached copy and validates the canonical result.
    /// </summary>
    public static ConfigurationValidationResult NormalizeAndValidate(AppConfiguration? configuration)
    {
        var structuralIssues = ValidateStructure(configuration);
        var normalized = ConfigurationNormalizer.Normalize(configuration);
        var issues = structuralIssues.Concat(Validate(normalized)).ToArray();
        return new ConfigurationValidationResult(normalized, issues);
    }

    private static IReadOnlyList<ConfigurationValidationIssue> ValidateStructure(AppConfiguration? configuration)
    {
        var issues = new List<ConfigurationValidationIssue>();
        if (configuration is null)
        {
            issues.Add(new ConfigurationValidationIssue("$", "Configuration is required."));
            return issues;
        }

        if (configuration.Entries is null)
        {
            issues.Add(new ConfigurationValidationIssue(
                nameof(AppConfiguration.Entries),
                "Entries collection is required."));
            return issues;
        }

        for (var index = 0; index < configuration.Entries.Count; index++)
        {
            var entry = configuration.Entries[index];
            var entryPath = $"{nameof(AppConfiguration.Entries)}[{index}]";
            if (entry is null)
            {
                issues.Add(new ConfigurationValidationIssue(entryPath, "Entry is required."));
            }
            else if (entry.WindowRule is null)
            {
                issues.Add(new ConfigurationValidationIssue(
                    $"{entryPath}.{nameof(LaunchEntry.WindowRule)}",
                    "Window rule is required."));
            }
        }

        return issues;
    }

    /// <summary>
    /// Validates without mutating or normalizing the supplied instance.
    /// </summary>
    public static IReadOnlyList<ConfigurationValidationIssue> Validate(AppConfiguration? configuration)
    {
        var issues = new List<ConfigurationValidationIssue>();

        if (configuration is null)
        {
            issues.Add(new ConfigurationValidationIssue("$", "Configuration is required."));
            return issues;
        }

        if (configuration.SchemaVersion != AppConfiguration.CurrentSchemaVersion)
        {
            issues.Add(new ConfigurationValidationIssue(
                nameof(AppConfiguration.SchemaVersion),
                $"Schema version must be {AppConfiguration.CurrentSchemaVersion}."));
        }

        if (configuration.GlobalTimeoutSeconds <= 0)
        {
            issues.Add(new ConfigurationValidationIssue(
                nameof(AppConfiguration.GlobalTimeoutSeconds),
                "Global timeout must be greater than zero."));
        }

        if (configuration.Entries is null)
        {
            issues.Add(new ConfigurationValidationIssue(
                nameof(AppConfiguration.Entries),
                "Entries collection is required."));
            return issues;
        }

        var ids = new HashSet<Guid>();
        for (var index = 0; index < configuration.Entries.Count; index++)
        {
            var entry = configuration.Entries[index];
            var entryPath = $"{nameof(AppConfiguration.Entries)}[{index}]";

            if (entry is null)
            {
                issues.Add(new ConfigurationValidationIssue(entryPath, "Entry is required."));
                continue;
            }

            ValidateEntry(entry, entryPath, ids, issues);
        }

        return issues;
    }

    private static void ValidateEntry(
        LaunchEntry entry,
        string entryPath,
        HashSet<Guid> ids,
        List<ConfigurationValidationIssue> issues)
    {
        if (entry.Id == Guid.Empty)
        {
            AddIssue(issues, entryPath, nameof(LaunchEntry.Id), "Id must not be empty.");
        }
        else if (!ids.Add(entry.Id))
        {
            AddIssue(issues, entryPath, nameof(LaunchEntry.Id), "Id must be unique.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            AddIssue(issues, entryPath, nameof(LaunchEntry.Name), "Name is required.");
        }

        if (!Enum.IsDefined(entry.LaunchKind))
        {
            AddIssue(
                issues,
                entryPath,
                nameof(LaunchEntry.LaunchKind),
                "Launch kind is not supported.");
        }
        else if (entry.LaunchKind == LaunchKind.Executable)
        {
            ValidateAbsolutePath(entry.ExecutablePath, entryPath, nameof(LaunchEntry.ExecutablePath), issues);
        }
        else
        {
            ValidateApplicationUserModelId(entry.ApplicationUserModelId, entryPath, issues);
        }

        if (!string.IsNullOrWhiteSpace(entry.WorkingDirectory))
        {
            ValidateAbsolutePath(
                entry.WorkingDirectory,
                entryPath,
                nameof(LaunchEntry.WorkingDirectory),
                issues);
        }

        if (!Enum.IsDefined(entry.ProcessMatchScope))
        {
            AddIssue(
                issues,
                entryPath,
                nameof(LaunchEntry.ProcessMatchScope),
                "Process match scope is not supported.");
        }
        else if (entry.ProcessMatchScope is ProcessMatchScope.ExactPath or ProcessMatchScope.Directory)
        {
            ValidateAbsolutePath(entry.MatchPath, entryPath, nameof(LaunchEntry.MatchPath), issues);
        }

        if (!Enum.IsDefined(entry.ExistingInstancePolicy))
        {
            AddIssue(
                issues,
                entryPath,
                nameof(LaunchEntry.ExistingInstancePolicy),
                "Existing instance policy is not supported.");
        }

        if (entry.TimeoutSeconds <= 0)
        {
            AddIssue(issues, entryPath, nameof(LaunchEntry.TimeoutSeconds), "Timeout must be greater than zero.");
        }

        if (entry.ExpectedMatches <= 0)
        {
            AddIssue(
                issues,
                entryPath,
                nameof(LaunchEntry.ExpectedMatches),
                "Expected matches must be greater than zero.");
        }

        if (entry.ActionDelayMilliseconds < 0)
        {
            AddIssue(
                issues,
                entryPath,
                nameof(LaunchEntry.ActionDelayMilliseconds),
                "Action delay must not be negative.");
        }

        if (!Enum.IsDefined(entry.Action))
        {
            AddIssue(issues, entryPath, nameof(LaunchEntry.Action), "Window action is not supported.");
        }

        if (entry.WindowRule is null)
        {
            AddIssue(issues, entryPath, nameof(LaunchEntry.WindowRule), "Window rule is required.");
        }
        else
        {
            ValidateRule(entry.WindowRule, $"{entryPath}.{nameof(LaunchEntry.WindowRule)}", issues);
        }
    }

    private static void ValidateRule(
        WindowRule rule,
        string rulePath,
        List<ConfigurationValidationIssue> issues)
    {
        if (!Enum.IsDefined(rule.TitleMatch))
        {
            AddIssue(issues, rulePath, nameof(WindowRule.TitleMatch), "Title match mode is not supported.");
        }
        else if (rule.TitleMatch != TitleMatchMode.Any && string.IsNullOrWhiteSpace(rule.TitlePattern))
        {
            AddIssue(
                issues,
                rulePath,
                nameof(WindowRule.TitlePattern),
                "A title pattern is required for this title match mode.");
        }
        else if (rule.TitleMatch == TitleMatchMode.Regex && !IsValidRegex(rule.TitlePattern!))
        {
            AddIssue(issues, rulePath, nameof(WindowRule.TitlePattern), "Title regular expression is invalid.");
        }

        ValidateNonNegative(rule.MinWidth, rulePath, nameof(WindowRule.MinWidth), issues);
        ValidateNonNegative(rule.MaxWidth, rulePath, nameof(WindowRule.MaxWidth), issues);
        ValidateNonNegative(rule.MinHeight, rulePath, nameof(WindowRule.MinHeight), issues);
        ValidateNonNegative(rule.MaxHeight, rulePath, nameof(WindowRule.MaxHeight), issues);

        if (rule.MinWidth > rule.MaxWidth)
        {
            AddIssue(issues, rulePath, nameof(WindowRule.MaxWidth), "Maximum width must not be less than minimum width.");
        }

        if (rule.MinHeight > rule.MaxHeight)
        {
            AddIssue(issues, rulePath, nameof(WindowRule.MaxHeight), "Maximum height must not be less than minimum height.");
        }
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateAbsolutePath(
        string? path,
        string entryPath,
        string propertyName,
        List<ConfigurationValidationIssue> issues)
    {
        if (!RuleMatcher.TryNormalizeWindowsPath(path, out _))
        {
            AddIssue(issues, entryPath, propertyName, "A fully qualified Windows path is required.");
        }
    }

    private static void ValidateApplicationUserModelId(
        string? applicationUserModelId,
        string entryPath,
        List<ConfigurationValidationIssue> issues)
    {
        var value = applicationUserModelId?.Trim();
        var separator = value?.IndexOf('!') ?? -1;
        if (string.IsNullOrEmpty(value) ||
            separator <= 0 ||
            separator != value.LastIndexOf('!') ||
            separator == value.Length - 1 ||
            value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            AddIssue(
                issues,
                entryPath,
                nameof(LaunchEntry.ApplicationUserModelId),
                "A non-empty application user model ID containing '!' is required.");
        }
    }

    private static void ValidateNonNegative(
        int? value,
        string rulePath,
        string propertyName,
        List<ConfigurationValidationIssue> issues)
    {
        if (value < 0)
        {
            AddIssue(issues, rulePath, propertyName, "Value must not be negative.");
        }
    }

    private static void AddIssue(
        List<ConfigurationValidationIssue> issues,
        string parentPath,
        string propertyName,
        string message)
    {
        issues.Add(new ConfigurationValidationIssue($"{parentPath}.{propertyName}", message));
    }
}
