using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ansight.Host.Runtime.RepositoryContracts;

namespace Ansight.Host.Runtime.Automation;

internal static class RepositoryAutomationTriggerLoader
{
    public const string TriggerDirectoryRelativePath = "ansight/triggers";

    private const int MaximumConditionsPerTrigger = 16;
    private const int MaximumFunctionTimeoutMilliseconds = 1_000;
    private const int MinimumFunctionTimeoutMilliseconds = 10;
    private const int MaximumRetryAttempts = 5;
    private const int MaximumRetryDelayMilliseconds = 60_000;
    private const int MinimumRetryDelayMilliseconds = 10;
    private const long MaximumModuleBytes = 1_048_576;
    private const int MaximumTriggersPerRepository = 256;
    private const string SupportedModuleExtension = ".ts";
    private static readonly JsonSerializerOptions triggerJson = new(JsonUtil.Compact)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static RepositoryAutomationCatalogLoadResult Load(
        IEnumerable<string> repositoryPaths,
        TimeSpan defaultFunctionTimeout,
        TimeSpan defaultActionTimeout)
        => Load(repositoryPaths, defaultFunctionTimeout, defaultActionTimeout, appId: null);

    public static RepositoryAutomationCatalogLoadResult Load(
        IEnumerable<string> repositoryPaths,
        TimeSpan defaultFunctionTimeout,
        TimeSpan defaultActionTimeout,
        string? appId)
    {
        var triggers = new List<RepositoryAutomationTriggerDefinition>();
        var warnings = new List<string>();

        foreach (var configuredPath in repositoryPaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.Ordinal))
        {
            try
            {
                triggers.AddRange(LoadRepository(
                    configuredPath,
                    defaultFunctionTimeout,
                    defaultActionTimeout,
                    appId));
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException
                                               or InvalidDataException
                                               or ArgumentException)
            {
                warnings.Add($"Repository triggers were not loaded from '{configuredPath}': {exception.Message}");
            }
        }

        return new RepositoryAutomationCatalogLoadResult(
            new RepositoryAutomationCatalog(triggers),
            warnings);
    }

    private static IReadOnlyList<RepositoryAutomationTriggerDefinition> LoadRepository(
        string configuredPath,
        TimeSpan defaultFunctionTimeout,
        TimeSpan defaultActionTimeout,
        string? scopedAppId)
    {
        var repositoryRootPath = Path.GetFullPath(configuredPath.Trim());
        if (!Directory.Exists(repositoryRootPath))
        {
            throw new DirectoryNotFoundException($"Repository root '{repositoryRootPath}' does not exist.");
        }

        var triggerDirectoryPath = Path.Combine(repositoryRootPath, "ansight", "triggers");
        if (!Directory.Exists(triggerDirectoryPath))
        {
            throw new DirectoryNotFoundException(
                $"The repository does not contain the visible '{TriggerDirectoryRelativePath}' directory.");
        }

        var modulePaths = Directory.EnumerateFiles(triggerDirectoryPath, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase))
            .Where(path => Path.GetExtension(path).Equals(
                SupportedModuleExtension,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (modulePaths.Length > MaximumTriggersPerRepository)
        {
            throw new InvalidDataException(
                $"The repository exceeds the {MaximumTriggersPerRepository}-trigger limit.");
        }

        if (modulePaths.Length == 0)
        {
            throw new InvalidDataException(
                $"The '{TriggerDirectoryRelativePath}' directory does not contain a .ts trigger module.");
        }

        var triggers = new List<RepositoryAutomationTriggerDefinition>(modulePaths.Length);
        var triggerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var modulePath in modulePaths)
        {
            triggers.Add(LoadTrigger(
                repositoryRootPath,
                triggerDirectoryPath,
                modulePath,
                scopedAppId,
                defaultFunctionTimeout,
                defaultActionTimeout,
                triggerIds));
        }

        return triggers;
    }

    private static RepositoryAutomationTriggerDefinition LoadTrigger(
        string repositoryRootPath,
        string triggerDirectoryPath,
        string modulePath,
        string? scopedAppId,
        TimeSpan defaultFunctionTimeout,
        TimeSpan defaultActionTimeout,
        HashSet<string> triggerIds)
    {
        var fileInfo = new FileInfo(modulePath);
        if (fileInfo.Length > MaximumModuleBytes)
        {
            throw new InvalidDataException(
                $"Trigger module '{modulePath}' exceeds the {MaximumModuleBytes}-byte limit.");
        }

        var triggerId = CreateTriggerId(triggerDirectoryPath, modulePath);
        if (!triggerIds.Add(triggerId))
        {
            throw new InvalidDataException($"Trigger id '{triggerId}' is duplicated.");
        }

        var source = File.ReadAllText(modulePath);
        var descriptorJson = RepositoryModuleDescriptorReader.ExtractObject(
            source,
            modulePath,
            "trigger",
            "Trigger");
        var definition = JsonSerializer.Deserialize<RepositoryAutomationTriggerModuleDefinition>(
            descriptorJson,
            triggerJson) ?? throw new InvalidDataException($"Trigger module '{modulePath}' has an empty descriptor.");
        var schemaVersion = ResolveSchemaVersion(definition.SchemaVersion, triggerId);
        definition.Requires?.Validate(schemaVersion);
        var appId = ResolveAppId(definition.AppId, scopedAppId, triggerId);
        var eventKind = NormalizeRequiredValue(definition.EventKind, $"Trigger '{triggerId}' eventKind");
        var eventSchema = LoadEventSchema(triggerId, definition.EventSchema);
        var functionTimeout = ResolveFunctionTimeout(
            definition.FunctionTimeoutMs,
            defaultFunctionTimeout,
            triggerId);
        var actionTimeout = ResolveActionTimeout(
            definition.ActionTimeoutSeconds,
            defaultActionTimeout,
            triggerId);
        var retryPolicy = ResolveRetryPolicy(definition.Retry, triggerId);
        var conditions = LoadConditions(triggerId, definition.Conditions);
        var automation = new RepositoryAutomationRegistration(
            repositoryRootPath,
            triggerId,
            RepositoryAutomationActionKind.TypeScript,
            modulePath,
            AppToolId: null,
            AppToolArguments: null,
            FunctionTimeout: functionTimeout,
            ActionTimeout: actionTimeout,
            RetryPolicy: retryPolicy);

        return new RepositoryAutomationTriggerDefinition(
            repositoryRootPath,
            triggerId,
            schemaVersion,
            appId,
            eventKind,
            eventSchema,
            automation,
            conditions)
        {
            Requires = definition.Requires,
            Enabled = definition.Enabled ?? true,
            ModulePath = modulePath
        };
    }

    private static int ResolveSchemaVersion(int? declaredVersion, string triggerId)
    {
        var version = declaredVersion ?? 1;
        if (version is not (1 or 2))
        {
            throw new InvalidDataException(
                $"Trigger '{triggerId}' schemaVersion must be 1 or 2.");
        }

        return version;
    }

    private static JsonObject? LoadEventSchema(string triggerId, JsonObject? schema)
    {
        if (schema is null)
        {
            return null;
        }

        if (schema["type"] is JsonValue typeValue
            && typeValue.TryGetValue<string>(out var schemaType)
            && !string.Equals(schemaType, "object", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Trigger '{triggerId}' eventSchema.type must be 'object'.");
        }

        return schema.DeepClone().AsObject();
    }

    private static IReadOnlyList<RepositoryAutomationTriggerCondition> LoadConditions(
        string triggerId,
        IReadOnlyList<RepositoryAutomationConditionDefinition>? entries)
    {
        entries ??= [];
        if (entries.Count > MaximumConditionsPerTrigger)
        {
            throw new InvalidDataException(
                $"Trigger '{triggerId}' exceeds the {MaximumConditionsPerTrigger}-condition limit.");
        }

        return entries.Select((entry, index) =>
        {
            if (entry is null)
            {
                throw new InvalidDataException(
                    $"Trigger '{triggerId}' condition {index + 1} must be an object.");
            }

            var field = NormalizeRequiredValue(
                entry.Field,
                $"Trigger '{triggerId}' condition {index + 1} field");
            if (!RepositoryAutomationTriggerCondition.IsSupportedField(field))
            {
                throw new InvalidDataException(
                    $"Trigger '{triggerId}' condition {index + 1} uses unsupported field '{field}'.");
            }

            var conditionOperator = NormalizeRequiredValue(
                entry.Operator,
                $"Trigger '{triggerId}' condition {index + 1} operator");
            if (!RepositoryAutomationTriggerCondition.IsSupportedOperator(conditionOperator))
            {
                throw new InvalidDataException(
                    $"Trigger '{triggerId}' condition {index + 1} uses unsupported operator '{conditionOperator}'.");
            }

            if (conditionOperator is not ("exists" or "notExists") && entry.Value is null)
            {
                throw new InvalidDataException(
                    $"Trigger '{triggerId}' condition {index + 1} must declare a value.");
            }

            if (conditionOperator is "contains" or "startsWith" or "endsWith"
                && !RepositoryAutomationTriggerCondition.IsStringValue(entry.Value))
            {
                throw new InvalidDataException(
                    $"Trigger '{triggerId}' condition {index + 1} operator '{conditionOperator}' requires a string value.");
            }

            return new RepositoryAutomationTriggerCondition(
                field,
                conditionOperator,
                entry.Value,
                entry.IgnoreCase);
        }).ToArray();
    }

    private static string ResolveAppId(string? declaredAppId, string? scopedAppId, string triggerId)
    {
        var normalizedDeclared = string.IsNullOrWhiteSpace(declaredAppId) ? null : declaredAppId.Trim();
        var normalizedScoped = string.IsNullOrWhiteSpace(scopedAppId) ? null : scopedAppId.Trim();
        if (normalizedScoped is not null
            && normalizedDeclared is not null
            && !string.Equals(normalizedScoped, normalizedDeclared, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Trigger '{triggerId}' declares appId '{normalizedDeclared}', which does not match the Ansight app ID '{normalizedScoped}'.");
        }

        return normalizedScoped
               ?? normalizedDeclared
               ?? throw new InvalidDataException(
                   $"Trigger '{triggerId}' must declare appId when it is loaded outside an Ansight app workspace.");
    }

    private static string CreateTriggerId(string triggerDirectoryPath, string modulePath)
    {
        var relativePath = Path.GetRelativePath(triggerDirectoryPath, modulePath);
        var withoutExtension = relativePath[..^Path.GetExtension(relativePath).Length];
        var id = withoutExtension
            .Replace(Path.DirectorySeparatorChar, '.')
            .Replace(Path.AltDirectorySeparatorChar, '.');
        return NormalizeRequiredIdentifier(id, "Trigger file name");
    }

    private static TimeSpan ResolveFunctionTimeout(
        int? timeoutMilliseconds,
        TimeSpan defaultTimeout,
        string triggerId)
    {
        if (!timeoutMilliseconds.HasValue)
        {
            return defaultTimeout;
        }

        if (timeoutMilliseconds is < MinimumFunctionTimeoutMilliseconds or > MaximumFunctionTimeoutMilliseconds)
        {
            throw new InvalidDataException(
                $"Trigger '{triggerId}' functionTimeoutMs must be between "
                + $"{MinimumFunctionTimeoutMilliseconds} and {MaximumFunctionTimeoutMilliseconds}.");
        }

        return TimeSpan.FromMilliseconds(timeoutMilliseconds.Value);
    }

    private static TimeSpan ResolveActionTimeout(
        int? timeoutSeconds,
        TimeSpan defaultTimeout,
        string triggerId)
    {
        if (!timeoutSeconds.HasValue)
        {
            return defaultTimeout;
        }

        if (timeoutSeconds is < 1 or > 300)
        {
            throw new InvalidDataException(
                $"Trigger '{triggerId}' actionTimeoutSeconds must be between 1 and 300.");
        }

        return TimeSpan.FromSeconds(timeoutSeconds.Value);
    }

    private static RepositoryAutomationRetryPolicy ResolveRetryPolicy(
        RepositoryAutomationRetryDefinition? definition,
        string triggerId)
    {
        if (definition is null)
        {
            return RepositoryAutomationRetryPolicy.None;
        }

        var maximumAttempts = definition.MaxAttempts ?? 1;
        if (maximumAttempts is < 1 or > MaximumRetryAttempts)
        {
            throw new InvalidDataException(
                $"Trigger '{triggerId}' retry.maxAttempts must be between 1 and {MaximumRetryAttempts}.");
        }

        if (maximumAttempts == 1)
        {
            if (definition.InitialDelayMs.HasValue
                || definition.BackoffMultiplier.HasValue
                || definition.MaxDelayMs.HasValue)
            {
                throw new InvalidDataException(
                    $"Trigger '{triggerId}' retry delays require retry.maxAttempts greater than 1.");
            }

            return RepositoryAutomationRetryPolicy.None;
        }

        var initialDelayMilliseconds = definition.InitialDelayMs ?? 250;
        if (initialDelayMilliseconds is < MinimumRetryDelayMilliseconds or > MaximumRetryDelayMilliseconds)
        {
            throw new InvalidDataException(
                $"Trigger '{triggerId}' retry.initialDelayMs must be between "
                + $"{MinimumRetryDelayMilliseconds} and {MaximumRetryDelayMilliseconds}.");
        }

        var backoffMultiplier = definition.BackoffMultiplier ?? 2;
        if (backoffMultiplier is < 1 or > 10)
        {
            throw new InvalidDataException(
                $"Trigger '{triggerId}' retry.backoffMultiplier must be between 1 and 10.");
        }

        var maximumDelayMilliseconds = definition.MaxDelayMs ?? Math.Min(
            MaximumRetryDelayMilliseconds,
            initialDelayMilliseconds * 8);
        if (maximumDelayMilliseconds < initialDelayMilliseconds
            || maximumDelayMilliseconds > MaximumRetryDelayMilliseconds)
        {
            throw new InvalidDataException(
                $"Trigger '{triggerId}' retry.maxDelayMs must be at least retry.initialDelayMs and no more than "
                + $"{MaximumRetryDelayMilliseconds}.");
        }

        return new RepositoryAutomationRetryPolicy(
            maximumAttempts,
            TimeSpan.FromMilliseconds(initialDelayMilliseconds),
            backoffMultiplier,
            TimeSpan.FromMilliseconds(maximumDelayMilliseconds));
    }

    private static string NormalizeRequiredIdentifier(string? value, string description)
    {
        var normalized = NormalizeRequiredValue(value, description);
        if (normalized.Length > 120
            || normalized.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new InvalidDataException(
                $"{description} may contain only letters, digits, hyphen, underscore, and period, up to 120 characters.");
        }

        return normalized;
    }

    private static string NormalizeRequiredValue(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{description} is required.");
        }

        return value.Trim();
    }
}
