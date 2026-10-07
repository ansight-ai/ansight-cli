using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Ansight.Host.Workspaces.Catalog;

public static class WorkspaceTestCatalog
{
    private const int ContractVersion = 1;
    private const int MaximumTestCount = 512;
    private const long MaximumTestFileBytes = 1_048_576;
    private const int MaximumRequiredSecretCount = 32;
    private const int MaximumHintTaskCount = 5;

    public static WorkspaceTestCatalogResult Load(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var fullWorkspacePath = Path.GetFullPath(workspacePath.Trim());
        var testsDirectoryPath = Path.Combine(fullWorkspacePath, "ansight", "tests");
        if (!Directory.Exists(testsDirectoryPath))
        {
            return new WorkspaceTestCatalogResult(fullWorkspacePath, [], []);
        }

        var tests = new List<WorkspaceTestDefinition>();
        var testIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        string[] testPaths;
        try
        {
            testPaths = Directory.EnumerateFiles(testsDirectoryPath, "*", SearchOption.AllDirectories)
                .Where(static path => IsTestExtension(Path.GetExtension(path)))
                .OrderBy(static path => path, GetPathComparer())
                .Take(MaximumTestCount + 1)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new WorkspaceTestCatalogResult(
                fullWorkspacePath,
                [],
                [$"Tests could not be read from '{testsDirectoryPath}': {exception.Message}"]);
        }

        foreach (var testPath in testPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tests.Count >= MaximumTestCount)
            {
                warnings.Add($"Only the first {MaximumTestCount:N0} workspace tests were loaded.");
                break;
            }

            try
            {
                var test = LoadTest(testsDirectoryPath, testPath);
                if (!testIds.Add(test.TestId))
                {
                    warnings.Add($"{Path.GetRelativePath(fullWorkspacePath, testPath)}: Duplicate test ID '{test.TestId}'.");
                    continue;
                }
                tests.Add(test);
                var instructionLength = test.BuildRunnerPrompt().Length;
                if (instructionLength > SimulatorAgentService.MaximumInstructionCharacters)
                {
                    warnings.Add(
                        $"{Path.GetRelativePath(fullWorkspacePath, testPath)}: Expanded runner instruction "
                        + $"contains {instructionLength:N0} characters; the maximum is "
                        + $"{SimulatorAgentService.MaximumInstructionCharacters:N0}.");
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or JsonException)
            {
                warnings.Add($"{Path.GetRelativePath(fullWorkspacePath, testPath)}: {exception.Message}");
            }
        }

        return new WorkspaceTestCatalogResult(fullWorkspacePath, tests, warnings);
    }

    public static WorkspaceTestDefinition Parse(
        string testsDirectoryPath,
        string testPath,
        string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testsDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(testPath);
        ArgumentNullException.ThrowIfNull(source);

        if (IsYamlExtension(Path.GetExtension(testPath)))
        {
            source = ConvertYamlToJson(source);
        }

        using var document = JsonDocument.Parse(source, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The test definition must be a JSON object.");
        }

        var schemaVersion = TryReadInt(root, "schemaVersion") ?? ContractVersion;
        if (schemaVersion != ContractVersion)
        {
            throw new InvalidDataException($"schemaVersion must be {ContractVersion}.");
        }

        var relativePath = Path.GetRelativePath(testsDirectoryPath, testPath).Replace('\\', '/');
        var inferredId = Path.ChangeExtension(relativePath, null)?.Replace('/', '.') ?? relativePath;
        var testId = NormalizeOptionalString(root, "id") ?? inferredId;
        var name = NormalizeOptionalString(root, "name")
                   ?? Humanize(Path.GetFileNameWithoutExtension(testPath));
        var appId = NormalizeRequiredString(root, "appId");
        var prompt = NormalizeRequiredString(root, "prompt");
        var validation = ReadValidation(root);
        var requiredSecrets = ReadRequiredSecrets(root);
        var hintTasks = ReadHintTasks(root);
        var taskId = NormalizeOptionalString(root, "taskId");
        if (string.IsNullOrWhiteSpace(validation.Prompt) && validation.Assertions.Count == 0)
        {
            throw new InvalidDataException(
                "validation must contain a prompt or at least one assertion.");
        }

        return new WorkspaceTestDefinition(
            testId,
            name,
            appId,
            prompt,
            validation,
            requiredSecrets,
            Path.GetFullPath(testPath))
        {
            Enabled = ReadEnabled(root),
            TaskId = taskId,
            HintTasks = hintTasks
        };
    }

    private static IReadOnlyList<string> ReadHintTasks(JsonElement root)
    {
        if (!root.TryGetProperty("hintTasks", out var hintsElement))
        {
            return [];
        }
        if (hintsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("hintTasks must be an array of repository task IDs.");
        }

        var taskIds = new List<string>();
        foreach (var hintElement in hintsElement.EnumerateArray())
        {
            if (hintElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(hintElement.GetString()))
            {
                throw new InvalidDataException("hintTasks must contain only non-empty repository task IDs.");
            }
            var taskId = hintElement.GetString()!.Trim();
            if (taskIds.Contains(taskId, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"hintTasks contains duplicate task ID '{taskId}'.");
            }
            taskIds.Add(taskId);
            if (taskIds.Count > MaximumHintTaskCount)
            {
                throw new InvalidDataException($"hintTasks can contain at most {MaximumHintTaskCount} task IDs.");
            }
        }
        return taskIds;
    }

    private static bool ReadEnabled(JsonElement root)
    {
        if (!root.TryGetProperty("enabled", out var enabledElement))
        {
            return true;
        }

        if (enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException("enabled must be a boolean.");
        }

        return enabledElement.GetBoolean();
    }

    private static WorkspaceTestDefinition LoadTest(string testsDirectoryPath, string testPath)
    {
        var fileInfo = new FileInfo(testPath);
        if (!fileInfo.Exists || fileInfo.Length > MaximumTestFileBytes)
        {
            throw new InvalidDataException("The test definition is missing or larger than 1 MB.");
        }

        return Parse(testsDirectoryPath, testPath, File.ReadAllText(testPath));
    }

    private static IReadOnlyList<string> ReadRequiredSecrets(JsonElement root)
    {
        if (!root.TryGetProperty("requiredSecrets", out var secretsElement))
        {
            return [];
        }

        if (secretsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("requiredSecrets must be an array of secret aliases.");
        }

        var aliases = new List<string>();
        foreach (var secretElement in secretsElement.EnumerateArray())
        {
            if (secretElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(secretElement.GetString()))
            {
                throw new InvalidDataException("requiredSecrets must contain only non-empty strings.");
            }

            var alias = secretElement.GetString()!.Trim();
            if (alias.Length > 128
                || !char.IsLetterOrDigit(alias[0])
                || alias.Any(static character => !char.IsLetterOrDigit(character)
                                                 && character is not '.' and not '_' and not '-'))
            {
                throw new InvalidDataException(
                    "requiredSecrets aliases must start with a letter or number and contain only letters, numbers, '.', '_' or '-' (maximum 128 characters).");
            }

            if (!aliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
            {
                aliases.Add(alias);
            }

            if (aliases.Count > MaximumRequiredSecretCount)
            {
                throw new InvalidDataException(
                    $"requiredSecrets can contain at most {MaximumRequiredSecretCount} aliases.");
            }
        }

        return aliases;
    }

    private static WorkspaceTestValidation ReadValidation(JsonElement root)
    {
        if (!root.TryGetProperty("validation", out var validationElement))
        {
            throw new InvalidDataException("validation is required.");
        }

        if (validationElement.ValueKind == JsonValueKind.String)
        {
            return new WorkspaceTestValidation(validationElement.GetString()?.Trim() ?? string.Empty, []);
        }

        if (validationElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("validation must be a string or JSON object.");
        }

        var prompt = NormalizeOptionalString(validationElement, "prompt") ?? string.Empty;
        var assertions = new List<string>();
        if (validationElement.TryGetProperty("assertions", out var assertionsElement))
        {
            if (assertionsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("validation.assertions must be an array of strings.");
            }

            foreach (var assertionElement in assertionsElement.EnumerateArray())
            {
                if (assertionElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(assertionElement.GetString()))
                {
                    throw new InvalidDataException(
                        "validation.assertions must contain only non-empty strings.");
                }

                assertions.Add(assertionElement.GetString()!.Trim());
            }
        }

        return new WorkspaceTestValidation(prompt, assertions);
    }

    private static string NormalizeRequiredString(JsonElement element, string propertyName)
        => NormalizeOptionalString(element, propertyName)
           ?? throw new InvalidDataException($"{propertyName} is required.");

    private static string? NormalizeOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static int? TryReadInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var result)
            ? result
            : null;

    private static string Humanize(string value)
    {
        var normalized = value.Replace('-', ' ').Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? "Workspace test"
            : char.ToUpperInvariant(normalized[0]) + normalized[1..];
    }

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool IsTestExtension(string extension)
        => extension.Equals(".json", StringComparison.OrdinalIgnoreCase) || IsYamlExtension(extension);

    private static bool IsYamlExtension(string extension)
        => extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase);

    private static string ConvertYamlToJson(string source)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(source));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                throw new InvalidDataException("The test definition must contain one YAML mapping.");
            }
            var nodeCount = 0;
            return ConvertYamlNode(root, string.Empty, 0, ref nodeCount).ToJsonString();
        }
        catch (YamlException exception)
        {
            throw new InvalidDataException($"Invalid YAML: {exception.Message}", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"Invalid YAML: {exception.Message}", exception);
        }
    }

    private static JsonNode ConvertYamlNode(YamlNode node, string propertyName, int depth, ref int nodeCount)
    {
        if (depth > 32 || ++nodeCount > 4096)
        {
            throw new InvalidDataException("YAML test definition is too complex.");
        }
        if (node is YamlMappingNode mapping)
        {
            var result = new JsonObject();
            foreach (var (key, value) in mapping.Children)
            {
                if (key is not YamlScalarNode { Value: { } name } || result.ContainsKey(name))
                {
                    throw new InvalidDataException("YAML mapping keys must be unique strings.");
                }
                result[name] = ConvertYamlNode(value, name, depth + 1, ref nodeCount);
            }
            return result;
        }
        if (node is YamlSequenceNode sequence)
        {
            var result = new JsonArray();
            foreach (var value in sequence.Children)
            {
                result.Add(ConvertYamlNode(value, propertyName, depth + 1, ref nodeCount));
            }
            return result;
        }
        if (node is not YamlScalarNode scalar || scalar.Value is null)
        {
            throw new InvalidDataException("YAML test values must be strings, arrays, or mappings.");
        }
        if (propertyName == "schemaVersion")
        {
            if (scalar.Style != ScalarStyle.Plain || !int.TryParse(scalar.Value, out var version))
            {
                throw new InvalidDataException("schemaVersion must be an integer.");
            }
            return JsonValue.Create(version)!;
        }
        if (propertyName == "enabled")
        {
            if (scalar.Style != ScalarStyle.Plain || !bool.TryParse(scalar.Value, out var enabled))
            {
                throw new InvalidDataException("enabled must be a boolean.");
            }
            return JsonValue.Create(enabled)!;
        }
        return JsonValue.Create(scalar.Value)!;
    }
}
