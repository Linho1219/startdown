namespace StartDown;

internal sealed record CommandLineOptions(
    bool Run,
    bool Startup,
    bool ShowStatus,
    Guid? EntryId,
    string? ConfigurationPath)
{
    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var run = false;
        var startup = false;
        var showStatus = false;
        Guid? entryId = null;
        string? configurationPath = null;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument.ToLowerInvariant())
            {
                case "--run":
                    run = true;
                    break;
                case "--startup":
                    run = true;
                    startup = true;
                    break;
                case "--show-status":
                    showStatus = true;
                    break;
                case "--entry":
                    if (++index >= args.Count || !Guid.TryParse(args[index], out var id))
                    {
                        throw new ArgumentException("--entry 后必须提供有效的 GUID。");
                    }
                    entryId = id;
                    run = true;
                    break;
                case "--config":
                    if (++index >= args.Count ||
                        string.IsNullOrWhiteSpace(args[index]) ||
                        args[index].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("--config 后必须提供配置文件路径。");
                    }
                    configurationPath = args[index];
                    break;
                default:
                    throw new ArgumentException($"未知命令行参数：{argument}");
            }
        }

        return new CommandLineOptions(run, startup, showStatus, entryId, configurationPath);
    }
}
