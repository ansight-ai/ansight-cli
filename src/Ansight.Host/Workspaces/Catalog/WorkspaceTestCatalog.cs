using System.Text.Json;

namespace Ansight.Host.Workspaces.Catalog;

public static class WorkspaceTestCatalog
{
    private const int ContractVersion = 1;
    private const int MaximumTestCount = 512;
    private const long MaximumTestFileBytes = 1_048_576;
    private const int MaximumRequiredSecretCount = 32;

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
        var warnings = new List<string>();
        string[] testPaths;
        try
        {
            testPaths = Directory.EnumerateFiles(
                    testsDirectoryPath,
                    "*.json",
                    SearchOption.AllDirectories)
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
            TaskId = taskId
        };
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
}
