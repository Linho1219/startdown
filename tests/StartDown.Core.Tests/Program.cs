using StartDown.Core;

var tests = new (string Name, Action Body)[]
{
    ("Model defaults", ModelDefaults),
    ("Default scope matches only launch path", DefaultScope),
    ("Default AUMID scope matches only launch identity", DefaultApplicationUserModelIdScope),
    ("Explicit path is exact and case-insensitive", ExplicitPath),
    ("Directory scope respects path boundaries", DirectoryBoundary),
    ("Title matching modes", TitleMatching),
    ("Invalid regex is a safe non-match", InvalidRegex),
    ("Dimension constraints are inclusive and combined", DimensionConstraints),
    ("Default structural window conditions", StructuralConditions),
    ("Configuration normalization and validation", ConfigurationProcessing),
    ("AUMID configuration normalization and validation", ApplicationUserModelIdConfiguration),
    ("Structural configuration cannot normalize into success", StructuralConfiguration),
};

var failures = new List<string>();
foreach (var (name, body) in tests)
{
    try
    {
        body();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL  {name}\n      {exception}");
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed.");
return failures.Count == 0 ? 0 : 1;

static void ModelDefaults()
{
    var configuration = new AppConfiguration();
    var entry = new LaunchEntry();
    var rule = new WindowRule();

    Assert.Equal(1, configuration.SchemaVersion);
    Assert.Equal(300, configuration.GlobalTimeoutSeconds);
    Assert.True(entry.Enabled);
    Assert.Equal(60, entry.TimeoutSeconds);
    Assert.Equal(1, entry.ExpectedMatches);
    Assert.Equal(250, entry.ActionDelayMilliseconds);
    Assert.Equal(LaunchKind.Executable, entry.LaunchKind);
    Assert.Null(entry.ApplicationUserModelId);
    Assert.Equal(ProcessMatchScope.ExactLaunchPath, entry.ProcessMatchScope);
    Assert.Equal(ExistingInstancePolicy.Skip, entry.ExistingInstancePolicy);
    Assert.Equal(WindowAction.Close, entry.Action);
    Assert.Equal(TitleMatchMode.Any, rule.TitleMatch);
    Assert.True(rule.RequireVisible);
    Assert.True(rule.RequireTopLevel);
    Assert.True(rule.RequireUnowned);
    Assert.True(rule.RequireNotMinimized);
}

static void DefaultApplicationUserModelIdScope()
{
    const string applicationUserModelId = "38833FF26BA1D.UnigramPreview_g9c9v27vpyspw!App";
    var entry = new LaunchEntry
    {
        Name = "Unigram",
        LaunchKind = LaunchKind.ApplicationUserModelId,
        ApplicationUserModelId = applicationUserModelId,
    };

    Assert.True(RuleMatcher.Matches(
        entry,
        Snapshot(@"C:\Program Files\WindowsApps\Unigram.exe", applicationUserModelId: applicationUserModelId)));
    Assert.True(RuleMatcher.Matches(
        entry,
        Snapshot(@"C:\Program Files\WindowsApps\Unigram.exe", applicationUserModelId: applicationUserModelId.ToLowerInvariant())));
    Assert.False(RuleMatcher.Matches(
        entry,
        Snapshot(@"C:\Program Files\WindowsApps\Unigram.exe", applicationUserModelId: "Other.Package_family!App")));
    Assert.False(RuleMatcher.Matches(
        entry,
        Snapshot(@"C:\Program Files\WindowsApps\Unigram.exe")));
}

static void DefaultScope()
{
    var entry = Entry(@"C:\Apps\Telegram\Telegram.exe");

    Assert.True(RuleMatcher.Matches(entry, Snapshot(@"c:\apps\telegram\TELEGRAM.EXE")));
    Assert.False(RuleMatcher.Matches(entry, Snapshot(@"C:\Apps\Telegram\Updater.exe")));
    Assert.False(RuleMatcher.Matches(entry, Snapshot(@"C:\Apps\Telegram2\Telegram.exe")));
}

static void ExplicitPath()
{
    var entry = Entry(@"C:\Launchers\AppLauncher.exe");
    entry.ProcessMatchScope = ProcessMatchScope.ExactPath;
    entry.MatchPath = @"C:\Apps\App\bin\v2\App.exe";

    Assert.True(RuleMatcher.Matches(entry, Snapshot(@"c:\apps\app\BIN\V2\app.exe")));
    Assert.False(RuleMatcher.Matches(entry, Snapshot(@"C:\Apps\App\bin\v3\App.exe")));
}

static void DirectoryBoundary()
{
    var entry = Entry(@"C:\Apps\QQ\Launcher.exe");
    entry.ProcessMatchScope = ProcessMatchScope.Directory;
    entry.MatchPath = @"C:\Apps\QQ";

    Assert.True(RuleMatcher.Matches(entry, Snapshot(@"C:\Apps\QQ\bin\v9.9\QQ.exe")));
    Assert.True(RuleMatcher.Matches(entry, Snapshot(@"c:/apps/qq/QQ.exe")));
    Assert.False(RuleMatcher.Matches(entry, Snapshot(@"C:\Apps\QQBeta\QQ.exe")));
    Assert.False(RuleMatcher.Matches(entry, Snapshot(@"C:\Apps\QQ.exe")));
    Assert.False(RuleMatcher.Matches(entry, Snapshot(@"C:\Apps\Other\QQ.exe")));
}

static void TitleMatching()
{
    var snapshot = Snapshot(@"C:\Apps\App.exe", title: "Telegram — Messages");

    Assert.True(RuleMatcher.MatchesWindow(new WindowRule(), snapshot));
    Assert.True(RuleMatcher.MatchesWindow(
        new WindowRule { TitleMatch = TitleMatchMode.Contains, TitlePattern = "telegram" },
        snapshot));
    Assert.False(RuleMatcher.MatchesWindow(
        new WindowRule { TitleMatch = TitleMatchMode.Contains, TitlePattern = "Settings" },
        snapshot));
    Assert.True(RuleMatcher.MatchesWindow(
        new WindowRule { TitleMatch = TitleMatchMode.Exact, TitlePattern = "telegram — messages" },
        snapshot));
    Assert.False(RuleMatcher.MatchesWindow(
        new WindowRule { TitleMatch = TitleMatchMode.Exact, TitlePattern = "Telegram" },
        snapshot));
    Assert.True(RuleMatcher.MatchesWindow(
        new WindowRule { TitleMatch = TitleMatchMode.Regex, TitlePattern = @"^Telegram\s+—\s+Messages$" },
        snapshot));

    Assert.True(RuleMatcher.MatchesWindow(
        new WindowRule { ClassName = "Qt5152QWindowIcon" },
        snapshot with { ClassName = "qt5152qwindowicon" }));
    Assert.False(RuleMatcher.MatchesWindow(
        new WindowRule { ClassName = "OtherClass" },
        snapshot));
}

static void InvalidRegex()
{
    var rule = new WindowRule
    {
        TitleMatch = TitleMatchMode.Regex,
        TitlePattern = "([unterminated",
    };

    var exception = Record.Exception(() => RuleMatcher.MatchesWindow(rule, Snapshot(@"C:\App.exe")));
    Assert.Null(exception);
    Assert.False(RuleMatcher.MatchesWindow(rule, Snapshot(@"C:\App.exe")));
}

static void DimensionConstraints()
{
    var rule = new WindowRule
    {
        MinWidth = 800,
        MaxWidth = 1200,
        MinHeight = 600,
        MaxHeight = 900,
    };

    Assert.True(RuleMatcher.MatchesWindow(rule, Snapshot(@"C:\App.exe", width: 800, height: 900)));
    Assert.True(RuleMatcher.MatchesWindow(rule, Snapshot(@"C:\App.exe", width: 1200, height: 600)));
    Assert.False(RuleMatcher.MatchesWindow(rule, Snapshot(@"C:\App.exe", width: 799, height: 700)));
    Assert.False(RuleMatcher.MatchesWindow(rule, Snapshot(@"C:\App.exe", width: 900, height: 901)));
}

static void StructuralConditions()
{
    var rule = new WindowRule();
    var ordinary = Snapshot(@"C:\App.exe");

    Assert.True(RuleMatcher.MatchesWindow(rule, ordinary));
    Assert.False(RuleMatcher.MatchesWindow(rule, ordinary with { IsVisible = false }));
    Assert.False(RuleMatcher.MatchesWindow(rule, ordinary with { IsCloaked = true }));
    Assert.False(RuleMatcher.MatchesWindow(rule, ordinary with { IsTopLevel = false }));
    Assert.False(RuleMatcher.MatchesWindow(rule, ordinary with { IsOwned = true }));
    Assert.False(RuleMatcher.MatchesWindow(rule, ordinary with { IsMinimized = true }));

    var permissive = new WindowRule
    {
        RequireVisible = false,
        RequireTopLevel = false,
        RequireUnowned = false,
        RequireNotMinimized = false,
    };

    Assert.True(RuleMatcher.MatchesWindow(
        permissive,
        ordinary with
        {
            IsVisible = false,
            IsCloaked = true,
            IsTopLevel = false,
            IsOwned = true,
            IsMinimized = true,
        }));
}

static void ConfigurationProcessing()
{
    var source = new AppConfiguration
    {
        Entries =
        [
            new LaunchEntry
            {
                Id = Guid.Empty,
                Name = "  Telegram  ",
                ExecutablePath = "  \"C:\\Apps\\Telegram\\Telegram.exe\"  ",
                ExistingInstancePolicy = ExistingInstancePolicy.Adopt,
                WindowRule = new WindowRule
                {
                    TitleMatch = TitleMatchMode.Contains,
                    TitlePattern = "Telegram",
                },
            },
        ],
    };

    var result = ConfigurationValidator.NormalizeAndValidate(source);
    Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
    Assert.Equal("Telegram", result.Configuration.Entries[0].Name);
    Assert.NotEqual(Guid.Empty, result.Configuration.Entries[0].Id);
    Assert.Equal(@"C:\Apps\Telegram\Telegram.exe", result.Configuration.Entries[0].ExecutablePath);
    Assert.Equal(LaunchKind.Executable, result.Configuration.Entries[0].LaunchKind);
    Assert.Null(result.Configuration.Entries[0].ApplicationUserModelId);
    Assert.Equal(ExistingInstancePolicy.Adopt, result.Configuration.Entries[0].ExistingInstancePolicy);
    Assert.Equal(Guid.Empty, source.Entries[0].Id, "Normalization must not mutate its input.");

    result.Configuration.Entries[0].ExistingInstancePolicy = (ExistingInstancePolicy)99;
    result.Configuration.Entries[0].WindowRule.TitleMatch = TitleMatchMode.Regex;
    result.Configuration.Entries[0].WindowRule.TitlePattern = "(";
    result.Configuration.Entries[0].WindowRule.MinWidth = 1000;
    result.Configuration.Entries[0].WindowRule.MaxWidth = 500;
    var issues = ConfigurationValidator.Validate(result.Configuration);

    Assert.True(issues.Any(issue => issue.Path.EndsWith("ExistingInstancePolicy", StringComparison.Ordinal)));
    Assert.True(issues.Any(issue => issue.Path.EndsWith("TitlePattern", StringComparison.Ordinal)));
    Assert.True(issues.Any(issue => issue.Path.EndsWith("MaxWidth", StringComparison.Ordinal)));
}

static void ApplicationUserModelIdConfiguration()
{
    var source = new AppConfiguration
    {
        Entries =
        [
            new LaunchEntry
            {
                Name = "Unigram",
                LaunchKind = LaunchKind.ApplicationUserModelId,
                ApplicationUserModelId = "  38833FF26BA1D.UnigramPreview_g9c9v27vpyspw!App  ",
                ExecutablePath = string.Empty,
            },
        ],
    };

    var result = ConfigurationValidator.NormalizeAndValidate(source);
    Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
    Assert.Equal(
        "38833FF26BA1D.UnigramPreview_g9c9v27vpyspw!App",
        result.Configuration.Entries[0].ApplicationUserModelId);
    Assert.Equal(
        "  38833FF26BA1D.UnigramPreview_g9c9v27vpyspw!App  ",
        source.Entries[0].ApplicationUserModelId,
        "Normalization must not mutate its input.");

    var missingId = ConfigurationValidator.NormalizeAndValidate(new AppConfiguration
    {
        Entries =
        [
            new LaunchEntry
            {
                Name = "Missing identity",
                LaunchKind = LaunchKind.ApplicationUserModelId,
            },
        ],
    });
    Assert.False(missingId.IsValid);
    Assert.True(missingId.Issues.Any(issue =>
        issue.Path.EndsWith(nameof(LaunchEntry.ApplicationUserModelId), StringComparison.Ordinal)));

    var malformedId = ConfigurationValidator.NormalizeAndValidate(new AppConfiguration
    {
        Entries =
        [
            new LaunchEntry
            {
                Name = "Malformed identity",
                LaunchKind = LaunchKind.ApplicationUserModelId,
                ApplicationUserModelId = "PackageWithoutApplicationSeparator",
            },
        ],
    });
    Assert.False(malformedId.IsValid);

    var unsupportedKind = ConfigurationValidator.NormalizeAndValidate(new AppConfiguration
    {
        Entries =
        [
            new LaunchEntry
            {
                Name = "Unsupported launch kind",
                LaunchKind = (LaunchKind)99,
            },
        ],
    });
    Assert.False(unsupportedKind.IsValid);
    Assert.True(unsupportedKind.Issues.Any(issue =>
        issue.Path.EndsWith(nameof(LaunchEntry.LaunchKind), StringComparison.Ordinal)));
}

static void StructuralConfiguration()
{
    Assert.False(ConfigurationValidator.NormalizeAndValidate(null).IsValid);

    var nullEntries = new AppConfiguration { Entries = null! };
    Assert.False(ConfigurationValidator.NormalizeAndValidate(nullEntries).IsValid);

    var nullEntry = new AppConfiguration { Entries = [null!] };
    Assert.False(ConfigurationValidator.NormalizeAndValidate(nullEntry).IsValid);

    var nullRule = new AppConfiguration
    {
        Entries =
        [
            new LaunchEntry
            {
                Name = "Unsafe",
                ExecutablePath = @"C:\Apps\Unsafe.exe",
                WindowRule = null!,
            },
        ],
    };
    Assert.False(ConfigurationValidator.NormalizeAndValidate(nullRule).IsValid);
}

static LaunchEntry Entry(string executablePath) => new()
{
    Name = "Test application",
    ExecutablePath = executablePath,
};

static WindowSnapshot Snapshot(
    string executablePath,
    string title = "Main window",
    int width = 1000,
    int height = 700,
    string? applicationUserModelId = null) => new(
        Hwnd: (nint)42,
        ProcessId: 1234,
        ExecutablePath: executablePath,
        Title: title,
        ClassName: "Qt5152QWindowIcon",
        Width: width,
        Height: height,
        IsVisible: true,
        IsTopLevel: true,
        IsOwned: false,
        IsCloaked: false,
        IsMinimized: false,
        ApplicationUserModelId: applicationUserModelId);

internal static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message ?? "Expected true, but was false.");
        }
    }

    public static void False(bool condition, string? message = null) =>
        True(!condition, message ?? "Expected false, but was true.");

    public static void Null(object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException($"Expected null, but was {value}.");
        }
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(message ?? $"Expected {expected}, but was {actual}.");
        }
    }

    public static void NotEqual<T>(T notExpected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
        {
            throw new InvalidOperationException($"Did not expect {notExpected}.");
        }
    }
}

internal static class Record
{
    public static Exception? Exception(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
