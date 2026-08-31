using StartDown.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartDown.Infrastructure;

internal sealed class ConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    private readonly AppLogger _logger;

    public ConfigurationStore(AppLogger logger, string? filePath = null)
    {
        _logger = logger;
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? AppPaths.ConfigurationFile
            : Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public AppConfiguration Load(bool requireExisting = false)
    {
        try
        {
            var json = File.ReadAllText(FilePath);
            var configuration = JsonSerializer.Deserialize<AppConfiguration>(json, SerializerOptions)
                ?? throw new JsonException("配置文件根节点不能是 null。");
            var validation = ConfigurationValidator.NormalizeAndValidate(configuration);
            if (!validation.IsValid)
            {
                throw new JsonException(
                    "配置内容无效：" + string.Join("; ", validation.Issues.Select(issue => $"{issue.Path}: {issue.Message}")));
            }
            return validation.Configuration;
        }
        catch (Exception exception) when (!requireExisting &&
                                             exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AppConfiguration();
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or JsonException
                                             or NotSupportedException
                                             or System.Security.SecurityException)
        {
            _logger.Error($"读取配置失败，原文件保持不变：{exception.Message}");
            throw new ConfigurationLoadException(FilePath, exception);
        }
    }

    public ConfigurationValidationResult Validate(AppConfiguration configuration) =>
        ConfigurationValidator.NormalizeAndValidate(configuration);

    public AppConfiguration Save(AppConfiguration configuration)
    {
        var validation = Validate(configuration);
        if (!validation.IsValid)
        {
            throw new ConfigurationValidationException(validation.Issues);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("配置文件必须具有父目录。"));
        var temporaryFile = FilePath + ".tmp";
        var json = JsonSerializer.Serialize(validation.Configuration, SerializerOptions);
        File.WriteAllText(temporaryFile, json);
        File.Move(temporaryFile, FilePath, overwrite: true);
        _logger.Info($"配置已保存到 {FilePath}");
        return validation.Configuration;
    }
}

internal sealed class ConfigurationLoadException : Exception
{
    public ConfigurationLoadException(string filePath, Exception innerException)
        : base($"无法读取配置文件“{filePath}”。原文件没有被修改。\n\n{innerException.Message}", innerException)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }
}

internal sealed class ConfigurationValidationException : Exception
{
    public ConfigurationValidationException(IReadOnlyList<ConfigurationValidationIssue> issues)
        : base(string.Join(Environment.NewLine, issues.Select(issue => $"{issue.Path}: {issue.Message}")))
    {
        Issues = issues;
    }

    public IReadOnlyList<ConfigurationValidationIssue> Issues { get; }
}
