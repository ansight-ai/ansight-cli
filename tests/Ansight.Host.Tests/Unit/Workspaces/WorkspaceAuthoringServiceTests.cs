using Ansight.Host.Runtime.Automation;
using Ansight.Host.Runtime.Tasks;
using Ansight.Host.Tests.Unit.Runtime;
using Ansight.Host.Workspaces;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class WorkspaceAuthoringServiceTests
{
    [Fact]
    public void Initialize_CreatesCompleteIdempotentWorkspaceSupportFiles()
    {
        using var directory = new TemporaryDirectory();
        var workspacePath = Path.Combine(directory.RootPath, "workspace");
        var service = new WorkspaceAuthoringService();

        var first = service.Initialize(new WorkspaceInitializeRequest(workspacePath));

        Assert.True(first.IsSuccess, first.Message);
        Assert.Equal(19, first.CreatedFiles.Count);
        Assert.Equal(
            "module",
            JsonDocument.Parse(File.ReadAllText(Path.Combine(workspacePath, "ansight", "package.json")))
                .RootElement.GetProperty("type").GetString());
        var readmeDocumentationUris = new Dictionary<string, string>
        {
            [string.Empty] = "https://www.ansight.ai/docs/workspace",
            ["tasks"] = "https://www.ansight.ai/docs/workspace/tasks",
            ["tests"] = "https://www.ansight.ai/docs/workspace/tests",
            ["triggers"] = "https://www.ansight.ai/docs/workspace/triggers",
            ["trends"] = "https://www.ansight.ai/docs/workspace/trends",
            ["sanitizers"] = "https://www.ansight.ai/docs/workspace/sanitizers",
            ["schema"] = "https://www.ansight.ai/docs/workspace/schemas"
        };
        foreach (var readmeDocumentationUri in readmeDocumentationUris)
        {
            var readmePath = Path.Combine(
                workspacePath,
                "ansight",
                readmeDocumentationUri.Key,
                "README.md");
            Assert.True(File.Exists(readmePath), $"Expected workspace guide at '{readmePath}'.");
            var readmeContent = File.ReadAllText(readmePath);
            Assert.StartsWith("# ", readmeContent, StringComparison.Ordinal);
            Assert.Contains($"]({readmeDocumentationUri.Value})", readmeContent, StringComparison.Ordinal);
        }
        Assert.True(Directory.Exists(Path.Combine(workspacePath, "ansight", "tests")));
        Assert.True(Directory.Exists(Path.Combine(workspacePath, "ansight", "trends")));
        Assert.True(Directory.Exists(Path.Combine(workspacePath, "ansight", "sanitizers")));
        var sanitizerTypesPath = Path.Combine(
            workspacePath,
            "ansight",
            "sanitizers",
            "ansight-sanitizer.d.ts");
        Assert.True(File.Exists(sanitizerTypesPath));
        Assert.Contains("NetworkRequestItem", File.ReadAllText(sanitizerTypesPath), StringComparison.Ordinal);
        Assert.Contains("NetworkBody", File.ReadAllText(sanitizerTypesPath), StringComparison.Ordinal);
        Assert.Contains("SanitizeNetworkRequest", File.ReadAllText(sanitizerTypesPath), StringComparison.Ordinal);
        var sanitizerTypes = File.ReadAllText(sanitizerTypesPath);
        Assert.Contains("SanitizerOperationContext", sanitizerTypes, StringComparison.Ordinal);
        Assert.Contains("SanitizerShareAudience", sanitizerTypes, StringComparison.Ordinal);
        Assert.Contains("audience: SanitizerShareAudience", sanitizerTypes, StringComparison.Ordinal);
        Assert.Contains("teamId: string", sanitizerTypes, StringComparison.Ordinal);
        Assert.DoesNotContain("isPublicWithoutSignIn", sanitizerTypes, StringComparison.Ordinal);
        var taskRuntimePath = Path.Combine(workspacePath, "ansight", "tasks", "ansight-task.js");
        Assert.True(File.Exists(taskRuntimePath));
        var taskRuntime = File.ReadAllText(taskRuntimePath);
        Assert.Contains("export const Permission = Object.freeze", taskRuntime, StringComparison.Ordinal);
        Assert.Contains("export const IosPermission = Object.freeze", taskRuntime, StringComparison.Ordinal);
        Assert.Contains("export const AndroidPermission = Object.freeze", taskRuntime, StringComparison.Ordinal);
        var taskTypesPath = Path.Combine(workspacePath, "ansight", "tasks", "ansight-task.d.ts");
        Assert.True(File.Exists(taskTypesPath));
        var taskTypes = File.ReadAllText(taskTypesPath);
        Assert.Contains(
            "enabled?: boolean",
            taskTypes,
            StringComparison.Ordinal);
        Assert.Contains("background(): Promise<AppLifecycleResult>", taskTypes, StringComparison.Ordinal);
        Assert.Contains("@supportedPlatforms ios android", taskTypes, StringComparison.Ordinal);
        Assert.Contains("@supportedDeviceKinds virtual", taskTypes, StringComparison.Ordinal);
        Assert.Contains("frameworks?: TaskFramework[]", taskTypes, StringComparison.Ordinal);
        Assert.Contains("@supportedFrameworks dotnet-maui", taskTypes, StringComparison.Ordinal);
        Assert.Contains("@unsupportedFrameworks dotnet-ios dotnet-android", taskTypes, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            workspacePath,
            "ansight",
            "triggers",
            "ansight-trigger.d.ts")));
        Assert.Contains(
            "enabled?: boolean",
            File.ReadAllText(Path.Combine(
                workspacePath,
                "ansight",
                "triggers",
                "ansight-trigger.d.ts")),
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(workspacePath, "ansight", "tasks", "tsconfig.json")));
        Assert.True(File.Exists(Path.Combine(workspacePath, "ansight", "triggers", "tsconfig.json")));
        Assert.True(File.Exists(Path.Combine(workspacePath, "ansight", "sanitizers", "tsconfig.json")));
        Assert.True(File.Exists(Path.Combine(
            workspacePath,
            "ansight",
            "schema",
            "trigger-definition.v1.schema.json")));
        Assert.True(File.Exists(Path.Combine(
            workspacePath,
            "ansight",
            "schema",
            "trends-definition.v1.schema.json")));
        foreach (var schemaFileName in new[]
                 {
                     "task-definition.v1.schema.json",
                     "test-definition.v1.schema.json",
                     "trigger-definition.v1.schema.json",
                     "trends-definition.v1.schema.json"
                 })
        {
            using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                workspacePath,
                "ansight",
                "schema",
                schemaFileName)));
            Assert.Equal(
                JsonValueKind.Object,
                schema.RootElement.GetProperty("properties").GetProperty("enabled").ValueKind);
        }
        foreach (var schemaPath in Directory.EnumerateFiles(
                     Path.Combine(workspacePath, "ansight", "schema"),
                     "*.json"))
        {
            using var _ = JsonDocument.Parse(File.ReadAllText(schemaPath));
        }

        var second = service.Initialize(new WorkspaceInitializeRequest(workspacePath));

        Assert.True(second.IsSuccess, second.Message);
        Assert.Empty(second.CreatedFiles);
        Assert.Empty(second.UpdatedFiles);
        Assert.Equal(19, second.ExistingFiles.Count);
    }

    [Fact]
    public void Initialize_DoesNotReplaceCustomizedSupportFileWithoutForce()
    {
        using var directory = new TemporaryDirectory();
        var service = new WorkspaceAuthoringService();
        var first = service.Initialize(new WorkspaceInitializeRequest(directory.RootPath));
        var taskTypesPath = Path.Combine(
            directory.RootPath,
            "ansight",
            "tasks",
            "ansight-task.d.ts");
        var tasksReadmePath = Path.Combine(
            directory.RootPath,
            "ansight",
            "tasks",
            "README.md");
        var taskRuntimePath = Path.Combine(directory.RootPath, "ansight", "tasks", "ansight-task.js");
        File.WriteAllText(taskRuntimePath, "// custom runtime constants");
        File.WriteAllText(taskTypesPath, "custom task types");
        File.WriteAllText(tasksReadmePath, "custom task guide");

        var preserved = service.Initialize(new WorkspaceInitializeRequest(directory.RootPath));

        Assert.True(first.IsSuccess, first.Message);
        Assert.True(preserved.IsSuccess, preserved.Message);
        Assert.Equal("// custom runtime constants", File.ReadAllText(taskRuntimePath));
        Assert.Equal("custom task types", File.ReadAllText(taskTypesPath));
        Assert.Equal("custom task guide", File.ReadAllText(tasksReadmePath));

        var replaced = service.Initialize(new WorkspaceInitializeRequest(
            directory.RootPath,
            OverwriteSupportFiles: true));

        Assert.True(replaced.IsSuccess, replaced.Message);
        Assert.Contains(taskRuntimePath, replaced.UpdatedFiles);
        Assert.Contains("export const Permission", File.ReadAllText(taskRuntimePath), StringComparison.Ordinal);
        Assert.Contains(taskTypesPath, replaced.UpdatedFiles);
        Assert.Contains(tasksReadmePath, replaced.UpdatedFiles);
        Assert.Contains("TaskDefinition", File.ReadAllText(taskTypesPath), StringComparison.Ordinal);
        Assert.StartsWith("# Tasks", File.ReadAllText(tasksReadmePath), StringComparison.Ordinal);
    }

    [Fact]
    public void AddDefinitions_CreatesFilesAcceptedByExistingCatalogs()
    {
        using var directory = new TemporaryDirectory();
        var service = new WorkspaceAuthoringService();

        var taskResult = service.AddTask(new WorkspaceTaskCreateRequest(
            directory.RootPath,
            "map.validate-areas",
            "Validate map areas",
            AppId: "com.example.app"));
        var testResult = service.AddTest(new WorkspaceTestCreateRequest(
            directory.RootPath,
            "map-smoke",
            "com.example.app",
            Assertions: ["The map is visible"],
            RequiredSecrets: ["MAP_TOKEN"]));
        var triggerResult = service.AddTrigger(new WorkspaceTriggerCreateRequest(
            directory.RootPath,
            "capture-errors",
            "session.log.received",
            "com.example.app"));
        var sanitizerResult = service.AddSanitizer(new WorkspaceSanitizerCreateRequest(
            directory.RootPath,
            "team-safe"));

        Assert.True(taskResult.IsSuccess, taskResult.Message);
        Assert.True(testResult.IsSuccess, testResult.Message);
        Assert.True(triggerResult.IsSuccess, triggerResult.Message);
        Assert.True(sanitizerResult.IsSuccess, sanitizerResult.Message);
        Assert.EndsWith("team-safe.ts", sanitizerResult.DefinitionPath, StringComparison.Ordinal);
        Assert.Contains(
            "export const sanitizeScreenshot: AnsightSanitizer.SanitizeScreenshot = async",
            File.ReadAllText(sanitizerResult.DefinitionPath!),
            StringComparison.Ordinal);
        Assert.Contains(
            "satisfies TaskDefinition",
            File.ReadAllText(taskResult.DefinitionPath!),
            StringComparison.Ordinal);
        Assert.Contains(
            "satisfies TriggerDefinition",
            File.ReadAllText(triggerResult.DefinitionPath!),
            StringComparison.Ordinal);
        var taskCatalog = RepositoryTaskLoader.Load(directory.RootPath, "com.example.app", _ => null);
        Assert.Empty(taskCatalog.Warnings);
        Assert.Contains(taskCatalog.Tasks, task => task.TaskId == "map.validate-areas");
        var testCatalog = WorkspaceTestCatalog.Load(directory.RootPath);
        Assert.Empty(testCatalog.Warnings);
        var test = Assert.Single(testCatalog.Tests);
        Assert.Equal("map-smoke", test.TestId);
        Assert.Equal(["MAP_TOKEN"], test.RequiredSecrets);
        var triggerCatalog = RepositoryAutomationTriggerLoader.Load(
            [directory.RootPath],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30),
            "com.example.app");
        Assert.Empty(triggerCatalog.Warnings);
        Assert.Contains(triggerCatalog.Catalog.Triggers, trigger => trigger.TriggerId == "capture-errors");
    }

    [Fact]
    public void AddTask_RequiresForceToReplaceExistingDefinition()
    {
        using var directory = new TemporaryDirectory();
        var service = new WorkspaceAuthoringService();
        var first = service.AddTask(new WorkspaceTaskCreateRequest(
            directory.RootPath,
            "smoke",
            Title: "Original"));

        var rejected = service.AddTask(new WorkspaceTaskCreateRequest(
            directory.RootPath,
            "smoke",
            Title: "Replacement"));
        var replaced = service.AddTask(new WorkspaceTaskCreateRequest(
            directory.RootPath,
            "smoke",
            Title: "Replacement",
            Overwrite: true));

        Assert.True(first.IsSuccess, first.Message);
        Assert.False(rejected.IsSuccess);
        Assert.Contains("--force", rejected.Message, StringComparison.Ordinal);
        Assert.True(replaced.IsSuccess, replaced.Message);
        Assert.Contains("Replacement", File.ReadAllText(replaced.DefinitionPath!), StringComparison.Ordinal);
    }
}
