using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.RepositoryContracts;

namespace Ansight.Host.Workspaces.Authoring;

public sealed class WorkspaceAuthoringService
{
    private static readonly JsonSerializerOptions indentedJson = new()
    {
        WriteIndented = true
    };

    public WorkspaceAuthoringResult Initialize(WorkspaceInitializeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workspacePath = NormalizeWorkspacePath(request.WorkspacePath);
        var createdFiles = new List<string>();
        var updatedFiles = new List<string>();
        var existingFiles = new List<string>();
        try
        {
            EnsureWorkspaceDirectory(workspacePath);
            var taskDirectoryPath = Path.Combine(workspacePath, "ansight", "tasks");
            var testDirectoryPath = Path.Combine(workspacePath, "ansight", "tests");
            var triggerDirectoryPath = Path.Combine(workspacePath, "ansight", "triggers");
            var trendsDirectoryPath = Path.Combine(workspacePath, "ansight", "trends");
            var sanitizerDirectoryPath = Path.Combine(workspacePath, "ansight", "sanitizers");
            var schemaDirectoryPath = Path.Combine(workspacePath, "ansight", "schema");
            Directory.CreateDirectory(taskDirectoryPath);
            Directory.CreateDirectory(testDirectoryPath);
            Directory.CreateDirectory(triggerDirectoryPath);
            Directory.CreateDirectory(trendsDirectoryPath);
            Directory.CreateDirectory(sanitizerDirectoryPath);
            Directory.CreateDirectory(schemaDirectoryPath);

            foreach (var readmeTemplate in WorkspaceReadmeTemplateArtifacts.All)
            {
                WriteSupportFile(
                    Path.Combine(
                        workspacePath,
                        "ansight",
                        readmeTemplate.RelativeDirectoryPath,
                        "README.md"),
                    readmeTemplate.Content,
                    request.OverwriteSupportFiles,
                    createdFiles,
                    updatedFiles,
                    existingFiles);
            }

            WriteSupportFile(
                Path.Combine(workspacePath, "ansight", "package.json"),
                CreateModulePackageManifest(),
                request.OverwriteSupportFiles,
                createdFiles,
                updatedFiles,
                existingFiles);
            WriteSupportFile(
                Path.Combine(taskDirectoryPath, "ansight-task.js"),
                RepositoryModuleContractArtifacts.GetTaskRuntimeModule(),
                request.OverwriteSupportFiles,
                createdFiles,
                updatedFiles,
                existingFiles);
            WriteSupportFile(
                Path.Combine(taskDirectoryPath, "ansight-task.d.ts"),
                RepositoryModuleContractArtifacts.GetTaskTypeDefinitions(),
                request.OverwriteSupportFiles,
                createdFiles,
                updatedFiles,
                existingFiles);
            WriteSupportFile(
                Path.Combine(taskDirectoryPath, "tsconfig.json"),
                CreateTypeScriptConfiguration(),
                request.OverwriteSupportFiles,
                createdFiles,
                updatedFiles,
                existingFiles);
            WriteSupportFile(
                Path.Combine(triggerDirectoryPath, "ansight-trigger.d.ts"),
                RepositoryModuleContractArtifacts.GetTriggerTypeDefinitions(),
                request.OverwriteSupportFiles,
                createdFiles,
                updatedFiles,
                existingFiles);
            WriteSupportFile(
                Path.Combine(triggerDirectoryPath, "tsconfig.json"),
                CreateTypeScriptConfiguration(),
                request.OverwriteSupportFiles,
                createdFiles,
                updatedFiles,
                existingFiles);
            WriteSupportFile(
                Path.Combine(schemaDirectoryPath, "task-definition.v1.schema.json"),
                RepositoryModuleContractArtifacts.GetTaskDefinitionSchema().ToJsonString(indentedJson),
                request.OverwriteSupportFiles,
                createdFiles,
                updatedFiles,
                existingFiles);
            WriteSupportFile(
                Path.Combine(schemaDirectoryPath, "trigger-definition.v1.schema.json"),
                RepositoryModuleContractArtifacts.GetTriggerDefinitionSchema().ToJsonString(indentedJson),
                request.OverwriteSupportFiles,
                createdFiles,
                updatedFiles,
                existingFiles);
            WriteSupportFile(
                Path.Combine(schemaDirectoryPath, "test-definition.v1.schema.json"),
                WorkspaceDefinitionSchemaArtifacts.TestDefinitionV1,
                request.OverwriteSupportFiles,
                createdFiles,
                updatedFiles,
                existingFiles);
            WriteSupportFile(
                Path.Combine(schemaDirectoryPath, "trends-definition.v1.schema.json"),
                WorkspaceDefinitionSchemaArtifacts.TrendsDefinitionV1,
                request.OverwriteSupportFiles,
                createdFiles,
                updatedFiles,
                existingFiles);
            WriteSupportFile(
                Path.Combine(sanitizerDirectoryPath, "ansight-sanitizer.d.ts"),
                SessionSanitizerModuleContractArtifacts.GetTypeDefinitions(),
                request.OverwriteSupportFiles,
                createdFiles,
                updatedFiles,
                existingFiles);
            WriteSupportFile(
                Path.Combine(sanitizerDirectoryPath, "tsconfig.json"),
                CreateTypeScriptConfiguration(),
                request.OverwriteSupportFiles,
                createdFiles,
                updatedFiles,
                existingFiles);

            return WorkspaceAuthoringResult.Success(
                $"Ansight workspace initialized at '{workspacePath}'.",
                workspacePath,
                definitionPath: null,
                createdFiles,
                updatedFiles,
                existingFiles);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return WorkspaceAuthoringResult.Failure(
                exception.Message,
                workspacePath,
                createdFiles: createdFiles,
                updatedFiles: updatedFiles,
                existingFiles: existingFiles);
        }
    }

    public WorkspaceAuthoringResult AddTask(WorkspaceTaskCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workspacePath = NormalizeWorkspacePath(request.WorkspacePath);
        var taskId = NormalizeDefinitionId(request.TaskId, "task ID", maximumLength: 160);
        var definitionPath = Path.Combine(workspacePath, "ansight", "tasks", $"{taskId}.ts");
        var title = NormalizeOptional(request.Title) ?? Humanize(taskId);
        var description = NormalizeOptional(request.Description)
                          ?? "Describe the repeatable workflow and its expected result.";
        var source = CreateTaskSource(title, description, NormalizeOptional(request.AppId));
        return AddDefinition(
            workspacePath,
            definitionPath,
            source,
            request.Overwrite,
            $"Task '{taskId}' created.");
    }

    public WorkspaceAuthoringResult AddTest(WorkspaceTestCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workspacePath = NormalizeWorkspacePath(request.WorkspacePath);
        var testId = NormalizeDefinitionId(request.TestId, "test ID", maximumLength: 160);
        var appId = NormalizeRequired(request.AppId, "app ID");
        var definitionPath = Path.Combine(workspacePath, "ansight", "tests", $"{testId}.yaml");
        foreach (var extension in new[] { ".json", ".yml" })
        {
            var existingPath = Path.ChangeExtension(definitionPath, extension);
            if (File.Exists(existingPath))
            {
                return WorkspaceAuthoringResult.Failure(
                    $"Test '{testId}' already exists at '{existingPath}'. Move or rename it before creating a YAML definition.",
                    workspacePath,
                    definitionPath,
                    existingFiles: [existingPath]);
            }
        }
        var source = CreateTestSource(request, testId, appId);
        WorkspaceTestCatalog.Parse(
            Path.Combine(workspacePath, "ansight", "tests"),
            definitionPath,
            source);
        return AddDefinition(
            workspacePath,
            definitionPath,
            source,
            request.Overwrite,
            $"Test '{testId}' created.");
    }

    public WorkspaceAuthoringResult AddTrigger(WorkspaceTriggerCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workspacePath = NormalizeWorkspacePath(request.WorkspacePath);
        var triggerId = NormalizeDefinitionId(request.TriggerId, "trigger ID", maximumLength: 120);
        var eventKind = NormalizeRequired(request.EventKind, "event kind");
        var definitionPath = Path.Combine(workspacePath, "ansight", "triggers", $"{triggerId}.ts");
        var source = CreateTriggerSource(eventKind, NormalizeOptional(request.AppId));
        return AddDefinition(
            workspacePath,
            definitionPath,
            source,
            request.Overwrite,
            $"Trigger '{triggerId}' created.");
    }

    public WorkspaceAuthoringResult AddSanitizer(WorkspaceSanitizerCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workspacePath = NormalizeWorkspacePath(request.WorkspacePath);
        var sanitizerId = NormalizeDefinitionId(request.SanitizerId, "sanitizer ID", maximumLength: 120);
        var definitionPath = Path.Combine(workspacePath, "ansight", "sanitizers", $"{sanitizerId}.ts");
        var source = SessionSanitizerModuleContractArtifacts.GetDefaultModuleSource();
        return AddDefinition(
            workspacePath,
            definitionPath,
            source,
            request.Overwrite,
            $"Sanitizer '{sanitizerId}' created.");
    }

    private WorkspaceAuthoringResult AddDefinition(
        string workspacePath,
        string definitionPath,
        string source,
        bool overwrite,
        string successMessage)
    {
        if (File.Exists(definitionPath) && !overwrite)
        {
            return WorkspaceAuthoringResult.Failure(
                $"Definition '{definitionPath}' already exists. Pass --force to replace it.",
                workspacePath,
                definitionPath,
                existingFiles: [definitionPath]);
        }

        var initialization = Initialize(new WorkspaceInitializeRequest(workspacePath));
        if (!initialization.IsSuccess)
        {
            return initialization;
        }

        var createdFiles = initialization.CreatedFiles.ToList();
        var updatedFiles = initialization.UpdatedFiles.ToList();
        var existingFiles = initialization.ExistingFiles.ToList();
        try
        {
            WriteDefinitionFile(
                definitionPath,
                source,
                overwrite,
                createdFiles,
                updatedFiles);
            return WorkspaceAuthoringResult.Success(
                successMessage,
                workspacePath,
                definitionPath,
                createdFiles,
                updatedFiles,
                existingFiles);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return WorkspaceAuthoringResult.Failure(
                exception.Message,
                workspacePath,
                definitionPath,
                createdFiles,
                updatedFiles,
                existingFiles);
        }
    }

    private static string CreateTaskSource(string title, string description, string? appId)
    {
        var appIdProperty = appId is null
            ? string.Empty
            : $"  \"appId\": {JsonSerializer.Serialize(appId)},\n";
        return EmbeddedTextResource.Render(
            "Workspaces/Authoring/Resources/task-template.ts",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["APP_ID_PROPERTY"] = appIdProperty,
                ["TITLE"] = JsonSerializer.Serialize(title),
                ["DESCRIPTION"] = JsonSerializer.Serialize(description)
            }).TrimEnd();
    }

    private static string CreateTestSource(
        WorkspaceTestCreateRequest request,
        string testId,
        string appId)
    {
        var assertions = NormalizeValues(request.Assertions);
        var requiredSecrets = NormalizeValues(request.RequiredSecrets);
        var source = new StringBuilder();
        source.AppendLine("# yaml-language-server: $schema=../schema/test-definition.v1.schema.json");
        source.AppendLine("schemaVersion: 1");
        source.AppendLine($"id: {JsonSerializer.Serialize(testId)}");
        source.AppendLine($"name: {JsonSerializer.Serialize(NormalizeOptional(request.Name) ?? Humanize(testId))}");
        source.AppendLine($"appId: {JsonSerializer.Serialize(appId)}");
        AppendYamlBlock(source, "prompt", NormalizeOptional(request.Prompt) ?? "Run the scenario using Ansight tools.");
        source.AppendLine("validation:");
        AppendYamlBlock(source, "  prompt", NormalizeOptional(request.ValidationPrompt) ?? "Validate the final app state.");
        if (assertions.Count > 0)
        {
            source.AppendLine("  assertions:");
            foreach (var assertion in assertions)
            {
                source.AppendLine($"    - {JsonSerializer.Serialize(assertion)}");
            }
        }
        if (requiredSecrets.Count > 0)
        {
            source.AppendLine("requiredSecrets:");
            foreach (var alias in requiredSecrets)
            {
                source.AppendLine($"  - {JsonSerializer.Serialize(alias)}");
            }
        }
        return source.ToString().TrimEnd();
    }

    private static void AppendYamlBlock(StringBuilder source, string key, string value)
    {
        var indentation = new string(' ', key.Length - key.TrimStart().Length + 2);
        source.AppendLine($"{key}: |-");
        foreach (var line in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            source.Append(indentation).AppendLine(line);
        }
    }

    private static string CreateTriggerSource(string eventKind, string? appId)
    {
        var appIdProperty = appId is null
            ? string.Empty
            : $"  \"appId\": {JsonSerializer.Serialize(appId)},\n";
        return EmbeddedTextResource.Render(
            "Workspaces/Authoring/Resources/trigger-template.ts",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["APP_ID_PROPERTY"] = appIdProperty,
                ["EVENT_KIND"] = JsonSerializer.Serialize(eventKind)
            }).TrimEnd();
    }

    private static string CreateTypeScriptConfiguration()
        => EmbeddedTextResource.Read("Workspaces/Authoring/Resources/tsconfig.json").TrimEnd();

    private static string CreateModulePackageManifest()
        => EmbeddedTextResource.Read("Workspaces/Authoring/Resources/package.json").TrimEnd();

    private static void WriteSupportFile(
        string filePath,
        string source,
        bool overwrite,
        ICollection<string> createdFiles,
        ICollection<string> updatedFiles,
        ICollection<string> existingFiles)
    {
        var normalizedSource = EnsureTrailingNewline(source);
        if (File.Exists(filePath))
        {
            if (string.Equals(File.ReadAllText(filePath), normalizedSource, StringComparison.Ordinal)
                || !overwrite)
            {
                existingFiles.Add(filePath);
                return;
            }

            WriteAtomically(filePath, normalizedSource);
            updatedFiles.Add(filePath);
            return;
        }

        WriteAtomically(filePath, normalizedSource);
        createdFiles.Add(filePath);
    }

    private static void WriteDefinitionFile(
        string filePath,
        string source,
        bool overwrite,
        ICollection<string> createdFiles,
        ICollection<string> updatedFiles)
    {
        var existed = File.Exists(filePath);
        if (existed && !overwrite)
        {
            throw new IOException($"Definition '{filePath}' already exists.");
        }

        WriteAtomically(filePath, EnsureTrailingNewline(source));
        if (existed)
        {
            updatedFiles.Add(filePath);
        }
        else
        {
            createdFiles.Add(filePath);
        }
    }

    private static void WriteAtomically(string filePath, string source)
    {
        var parentPath = Path.GetDirectoryName(filePath)
                         ?? throw new IOException($"A parent directory could not be resolved for '{filePath}'.");
        Directory.CreateDirectory(parentPath);
        var temporaryPath = Path.Combine(parentPath, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporaryPath,
                source,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string NormalizeWorkspacePath(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        return Path.GetFullPath(workspacePath.Trim());
    }

    private static void EnsureWorkspaceDirectory(string workspacePath)
    {
        if (File.Exists(workspacePath))
        {
            throw new IOException($"Workspace path '{workspacePath}' is a file.");
        }

        Directory.CreateDirectory(workspacePath);
    }

    private static string NormalizeDefinitionId(string value, string description, int maximumLength)
    {
        var normalized = NormalizeRequired(value, description);
        if (normalized.Length > maximumLength
            || !char.IsLetterOrDigit(normalized[0])
            || !char.IsLetterOrDigit(normalized[^1])
            || normalized.Contains("..", StringComparison.Ordinal)
            || normalized.Any(character => !(char.IsLetterOrDigit(character)
                                              || character is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                $"{description} must start and end with a letter or digit and contain only letters, digits, "
                + $"hyphen, underscore, and period, up to {maximumLength} characters.",
                description);
        }

        return normalized;
    }

    private static string NormalizeRequired(string? value, string description)
        => NormalizeOptional(value) ?? throw new ArgumentException($"{description} is required.", description);

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> NormalizeValues(IReadOnlyList<string>? values)
        => values is null
            ? []
            : values
                .Select(NormalizeOptional)
                .Where(static value => value is not null)
                .Select(static value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static string Humanize(string value)
    {
        var words = value.Replace('.', ' ').Replace('-', ' ').Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0
            ? value
            : string.Join(' ', words.Select(static word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static string EnsureTrailingNewline(string source)
        => source.EndsWith('\n') ? source : source + Environment.NewLine;
}
