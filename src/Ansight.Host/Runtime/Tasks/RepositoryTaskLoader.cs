using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ansight.Host.Runtime.Automation;
using Ansight.Host.Runtime.RepositoryContracts;

namespace Ansight.Host.Runtime.Tasks;

internal static class RepositoryTaskLoader
{
    public const string TaskDirectoryRelativePath = "ansight/tasks";

    internal const int DefaultMaximumActionsPerTask = 64;
    private const int MaximumActionsPerTask = 100;
    private const int MaximumDeclaredHostToolsPerTask = 32;
    private const int MaximumKeywordsPerTask = 32;
    private const long MaximumModuleBytes = 1_048_576;
    private const int MaximumTasksPerRepository = 256;
    private const string SupportedModuleExtension = ".ts";
    private static readonly JsonSerializerOptions taskJson = new(JsonUtil.Compact)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static RepositoryTaskLoadResult Load(
        string repositoryPath,
        string appId,
        RepositoryTaskHostToolResolver hostToolResolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(hostToolResolver);

        var repositoryRootPath = Path.GetFullPath(repositoryPath.Trim());
        if (!Directory.Exists(repositoryRootPath))
        {
            return new RepositoryTaskLoadResult(
                [],
                [$"Repository root '{repositoryRootPath}' does not exist."]);
        }

        var taskDirectoryPath = Path.Combine(repositoryRootPath, TaskDirectoryRelativePath);
        if (!Directory.Exists(taskDirectoryPath))
        {
            return new RepositoryTaskLoadResult([], []);
        }

        var modulePaths = Directory.EnumerateFiles(taskDirectoryPath, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(
                SupportedModuleExtension,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var tasks = new List<RepositoryTaskDefinition>();
        var supportModules = new List<RepositoryTaskSupportModule>();
        var warnings = new List<string>();
        var taskIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var modulePath in modulePaths)
        {
            try
            {
                if (new FileInfo(modulePath).Length > MaximumModuleBytes)
                {
                    throw new InvalidDataException(
                        $"Task or support module '{modulePath}' exceeds the {MaximumModuleBytes}-byte limit.");
                }

                var source = File.ReadAllText(modulePath);
                if (modulePath.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)
                    || !RepositoryModuleDescriptorReader.ContainsExportedDescriptor(source, "task"))
                {
                    var relativePath = Path.GetRelativePath(taskDirectoryPath, modulePath)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    if (!string.Equals(relativePath, "ansight-task.d.ts", StringComparison.OrdinalIgnoreCase))
                    {
                        supportModules.Add(new RepositoryTaskSupportModule(
                            relativePath,
                            source,
                            modulePath.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)));
                    }
                    continue;
                }

                if (tasks.Count >= MaximumTasksPerRepository)
                {
                    throw new InvalidDataException(
                        $"The repository exceeds the {MaximumTasksPerRepository}-task limit.");
                }

                var task = LoadTask(
                    repositoryRootPath,
                    taskDirectoryPath,
                    modulePath,
                    appId.Trim(),
                    hostToolResolver);
                if (!taskIds.Add(task.TaskId))
                {
                    throw new InvalidDataException($"Task id '{task.TaskId}' is duplicated.");
                }

                tasks.Add(task);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException
                                               or InvalidDataException
                                               or ArgumentException)
            {
                warnings.Add($"Repository module '{modulePath}' was not loaded: {exception.Message}");
            }
        }

        return new RepositoryTaskLoadResult(tasks, warnings)
        {
            SupportModules = supportModules
        };
    }

    public static IReadOnlyList<RepositoryTaskSupportModule> DiscoverSupportModules(
        string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var taskDirectoryPath = Path.Combine(
            Path.GetFullPath(repositoryPath.Trim()),
            TaskDirectoryRelativePath);
        if (!Directory.Exists(taskDirectoryPath))
        {
            return [];
        }

        var supportModules = new List<RepositoryTaskSupportModule>();
        foreach (var path in Directory.EnumerateFiles(taskDirectoryPath, "*", SearchOption.AllDirectories)
                     .Where(path => Path.GetExtension(path).Equals(
                         SupportedModuleExtension,
                         StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                if (new FileInfo(path).Length > MaximumModuleBytes)
                {
                    continue;
                }

                var source = File.ReadAllText(path);
                var isDeclaration = path.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase);
                if (!isDeclaration
                    && RepositoryModuleDescriptorReader.ContainsExportedDescriptor(source, "task"))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(taskDirectoryPath, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!string.Equals(relativePath, "ansight-task.d.ts", StringComparison.OrdinalIgnoreCase))
                {
                    supportModules.Add(new RepositoryTaskSupportModule(relativePath, source, isDeclaration));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Task discovery reports unreadable modules as warnings. Support discovery is advisory,
                // so one unreadable helper must not prevent the remaining modules from being available.
            }
        }

        return supportModules;
    }

    private static RepositoryTaskDefinition LoadTask(
        string repositoryRootPath,
        string taskDirectoryPath,
        string modulePath,
        string appId,
        RepositoryTaskHostToolResolver hostToolResolver)
    {
        if (new FileInfo(modulePath).Length > MaximumModuleBytes)
        {
            throw new InvalidDataException(
                $"Task module '{modulePath}' exceeds the {MaximumModuleBytes}-byte limit.");
        }

        var taskId = CreateTaskId(taskDirectoryPath, modulePath);
        var source = File.ReadAllText(modulePath);
        var descriptorJson = RepositoryModuleDescriptorReader.ExtractObject(
            source,
            modulePath,
            "task",
            "Task");
        var definition = JsonSerializer.Deserialize<RepositoryTaskModuleDefinition>(
            descriptorJson,
            taskJson) ?? throw new InvalidDataException($"Task module '{modulePath}' has an empty descriptor.");
        var schemaVersion = ResolveSchemaVersion(definition.SchemaVersion, taskId);
        definition.Requires?.Validate(schemaVersion);
        var declaredAppId = NormalizeOptionalValue(definition.AppId);
        if (declaredAppId is not null
            && !string.Equals(declaredAppId, appId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Task '{taskId}' declares appId '{declaredAppId}', which does not match Ansight app ID '{appId}'.");
        }

        var title = NormalizeRequiredValue(definition.Title, $"Task '{taskId}' title");
        var description = NormalizeRequiredValue(definition.Description, $"Task '{taskId}' description");
        var feature = NormalizeOptionalValue(definition.Feature);
        var keywords = LoadKeywords(taskId, definition.Keywords);
        var inputSchema = LoadInputSchema(taskId, definition.InputSchema);
        var outputSchema = definition.OutputSchema?.DeepClone().AsObject();
        var declaredHostTools = LoadDeclaredHostTools(taskId, definition.HostTools, hostToolResolver);
        var platforms = LoadTargetMetadata(taskId, "platforms", definition.Platforms, RepositoryTaskTargets.Platforms);
        var deviceKinds = LoadTargetMetadata(taskId, "deviceKinds", definition.DeviceKinds, RepositoryTaskTargets.DeviceKinds);
        var frameworks = LoadTargetMetadata(taskId, "frameworks", definition.Frameworks, RepositoryTaskTargets.Frameworks);
        var timeoutSeconds = definition.TimeoutSeconds ?? 120;
        if (timeoutSeconds is < 1 or > 300)
        {
            throw new InvalidDataException(
                $"Task '{taskId}' timeoutSeconds must be between 1 and 300.");
        }

        var maximumActions = definition.MaximumActions ?? DefaultMaximumActionsPerTask;
        if (maximumActions is < 1 or > MaximumActionsPerTask)
        {
            throw new InvalidDataException(
                $"Task '{taskId}' maximumActions must be between 1 and {MaximumActionsPerTask}.");
        }

        return new RepositoryTaskDefinition(
            repositoryRootPath,
            modulePath,
            taskId,
            schemaVersion,
            appId,
            title,
            description,
            feature,
            keywords,
            inputSchema,
            outputSchema,
            declaredHostTools,
            TimeSpan.FromSeconds(timeoutSeconds),
            maximumActions)
        {
            Requires = definition.Requires,
            Enabled = definition.Enabled ?? true,
            Platforms = platforms,
            DeviceKinds = deviceKinds,
            Frameworks = frameworks
        };
    }

    private static IReadOnlyList<string> LoadTargetMetadata(
        string taskId,
        string propertyName,
        IReadOnlyList<string>? entries,
        IReadOnlyList<string> allowedValues)
    {
        if (entries is null)
        {
            return [];
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException(
                $"Task '{taskId}' {propertyName} must contain at least one value when supplied.");
        }

        var values = new List<string>();
        foreach (var entry in entries)
        {
            var value = NormalizeOptionalValue(entry)?.ToLowerInvariant();
            if (value is null || !allowedValues.Contains(value, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Task '{taskId}' {propertyName} must contain only: {string.Join(", ", allowedValues)}.");
            }

            if (!values.Contains(value, StringComparer.Ordinal))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static int ResolveSchemaVersion(int? declaredVersion, string taskId)
    {
        var version = declaredVersion ?? 1;
        if (version is not (1 or 2))
        {
            throw new InvalidDataException(
                $"Task '{taskId}' schemaVersion must be 1 or 2.");
        }

        return version;
    }

    private static IReadOnlyList<string> LoadKeywords(string taskId, IReadOnlyList<string>? entries)
    {
        entries ??= [];
        if (entries.Count > MaximumKeywordsPerTask)
        {
            throw new InvalidDataException(
                $"Task '{taskId}' exceeds the {MaximumKeywordsPerTask}-keyword limit.");
        }

        return entries
            .Select(NormalizeOptionalValue)
            .Where(static keyword => keyword is not null)
            .Select(static keyword => keyword!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static JsonObject LoadInputSchema(string taskId, JsonObject? schema)
    {
        schema ??= new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["additionalProperties"] = false
        };
        if (!string.Equals(schema["type"]?.GetValue<string>(), "object", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Task '{taskId}' inputSchema.type must be 'object'.");
        }

        if (schema["properties"] is not null && schema["properties"] is not JsonObject)
        {
            throw new InvalidDataException($"Task '{taskId}' inputSchema.properties must be an object.");
        }

        return schema.DeepClone().AsObject();
    }

    private static IReadOnlyDictionary<string, RepositoryTaskHostToolDescriptor> LoadDeclaredHostTools(
        string taskId,
        IReadOnlyList<string>? entries,
        RepositoryTaskHostToolResolver hostToolResolver)
    {
        entries ??= [];
        if (entries.Count > MaximumDeclaredHostToolsPerTask)
        {
            throw new InvalidDataException(
                $"Task '{taskId}' exceeds the {MaximumDeclaredHostToolsPerTask}-host-tool limit.");
        }

        var hostTools = new Dictionary<string, RepositoryTaskHostToolDescriptor>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var toolName = NormalizeRequiredIdentifier(
                entry,
                $"Task '{taskId}' hostTools entry");
            var descriptor = hostToolResolver(toolName)
                             ?? throw new InvalidDataException(
                                 $"Task '{taskId}' declares host tool '{toolName}', which is unknown or not task-callable.");
            hostTools.TryAdd(toolName, descriptor);
        }

        return hostTools;
    }

    private static string CreateTaskId(string taskDirectoryPath, string modulePath)
    {
        var relativePath = Path.GetRelativePath(taskDirectoryPath, modulePath);
        var withoutExtension = relativePath[..^Path.GetExtension(relativePath).Length];
        var id = withoutExtension
            .Replace(Path.DirectorySeparatorChar, '.')
            .Replace(Path.AltDirectorySeparatorChar, '.');
        return NormalizeRequiredIdentifier(id, "Task file name");
    }

    private static string NormalizeRequiredIdentifier(string? value, string description)
    {
        var normalized = NormalizeRequiredValue(value, description);
        if (normalized.Length > 160
            || normalized.Any(character => !(char.IsLetterOrDigit(character)
                                              || character is '-' or '_' or '.')))
        {
            throw new InvalidDataException(
                $"{description} may contain only letters, digits, hyphen, underscore, and period, up to 160 characters.");
        }

        return normalized;
    }

    private static string NormalizeRequiredValue(string? value, string description)
        => NormalizeOptionalValue(value)
           ?? throw new InvalidDataException($"{description} is required.");

    private static string? NormalizeOptionalValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
