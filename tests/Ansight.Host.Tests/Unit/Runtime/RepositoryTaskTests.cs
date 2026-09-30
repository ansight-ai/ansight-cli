using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.RepositoryContracts;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryTaskTests
{
    [Fact]
    public void Loader_IndexesStaticTaskDescriptorsAndIgnoresTypeDeclarations()
    {
        using var repository = CreateRepository(CreatePassingModule());
        File.WriteAllText(
            Path.Combine(repository.RootPath, "ansight", "tasks", "ansight-task.d.ts"),
            "export type Task = unknown;");

        var result = LoadRepository(repository.RootPath);

        Assert.Empty(result.Warnings);
        var task = Assert.Single(result.Tasks);
        Assert.Equal("map.validate", task.TaskId);
        Assert.Equal(1, task.SchemaVersion);
        Assert.Equal("Validate map", task.Title);
        Assert.Equal("map", task.Feature);
        Assert.Empty(task.DeclaredHostTools);
        Assert.Equal(TimeSpan.FromSeconds(10), task.Timeout);
        Assert.Equal(4, task.MaximumActions);
        Assert.Equal("object", task.OutputSchema?["type"]?.GetValue<string>());
        Assert.Equal(task.ModulePath, task.ToPublicDefinition().ModulePath);
    }

    [Fact]
    public void Loader_ValidatesAndPublishesOptionalTargetMetadata()
    {
        using var repository = CreateRepository(CreatePassingModule().Replace(
            "\"title\": \"Validate map\",",
            "\"title\": \"Validate map\",\n             \"platforms\": [\"ios\"],\n             \"deviceKinds\": [\"physical\"],\n             \"frameworks\": [\"dotnet-ios\", \"ios-uikit\"],",
            StringComparison.Ordinal));

        var result = LoadRepository(repository.RootPath);

        Assert.Empty(result.Warnings);
        var task = Assert.Single(result.Tasks);
        Assert.Equal(["ios"], task.Platforms);
        Assert.Equal(["physical"], task.DeviceKinds);
        Assert.Equal(["dotnet-ios", "ios-uikit"], task.Frameworks);
        Assert.Equal(task.Frameworks, task.ToPublicDefinition().Frameworks);
    }

    [Theory]
    [InlineData("platforms", "windows")]
    [InlineData("deviceKinds", "desktop")]
    [InlineData("frameworks", "maui")]
    public void Loader_RejectsUnsupportedTargetMetadata(string propertyName, string value)
    {
        using var repository = CreateRepository(CreatePassingModule().Replace(
            "\"title\": \"Validate map\",",
            $"\"title\": \"Validate map\",\n             \"{propertyName}\": [\"{value}\"],",
            StringComparison.Ordinal));

        var result = LoadRepository(repository.RootPath);

        Assert.Empty(result.Tasks);
        Assert.Contains(
            $"{propertyName} must contain only",
            Assert.Single(result.Warnings),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TargetMetadata_InfersPlatformsForPlatformSpecificFrameworks()
    {
        Assert.Equal(
            DevicePlatforms.Ios,
            RepositoryTaskTargets.ResolveRequiredPlatform([RepositoryTaskTargets.DotNetIos]));
        Assert.Equal(
            DevicePlatforms.Android,
            RepositoryTaskTargets.ResolveRequiredPlatform([RepositoryTaskTargets.DotNetAndroid]));
        Assert.Null(RepositoryTaskTargets.ResolveRequiredPlatform([RepositoryTaskTargets.DotNetMaui]));
        Assert.True(RepositoryTaskTargets.SupportsAnyFramework(
            [RepositoryTaskTargets.DotNetMaui],
            ["maui"]));
        Assert.True(RepositoryTaskTargets.SupportsAnyFramework(
            [RepositoryTaskTargets.DotNetIos],
            [RepositoryTaskTargets.IosSwiftUi],
            DevicePlatforms.Ios));
        Assert.False(RepositoryTaskTargets.SupportsAnyFramework(
            [RepositoryTaskTargets.DotNetIos],
            [RepositoryTaskTargets.AndroidViews],
            DevicePlatforms.Android));
    }

    [Fact]
    public void Loader_ClassifiesTypeScriptWithoutTaskDescriptorAsSupportModules()
    {
        using var repository = CreateRepository(CreatePassingModule());
        var taskDirectory = Path.Combine(repository.RootPath, "ansight", "tasks");
        File.WriteAllText(
            Path.Combine(taskDirectory, "map", "helpers.ts"),
            "export function openOfflineArea() { return 'opened'; }");
        File.WriteAllText(
            Path.Combine(taskDirectory, "domain.d.ts"),
            "export interface OfflineArea { id: string; }");

        var result = LoadRepository(repository.RootPath);

        Assert.Empty(result.Warnings);
        Assert.Equal("map.validate", Assert.Single(result.Tasks).TaskId);
        Assert.Collection(
            result.SupportModules.OrderBy(module => module.RelativePath, StringComparer.Ordinal),
            declaration =>
            {
                Assert.Equal("domain.d.ts", declaration.RelativePath);
                Assert.True(declaration.IsDeclaration);
            },
            helper =>
            {
                Assert.Equal("map/helpers.ts", helper.RelativePath);
                Assert.False(helper.IsDeclaration);
                Assert.Contains("openOfflineArea", helper.Source, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Loader_DoesNotMistakeTaskExportTextForATaskDescriptor()
    {
        using var repository = CreateRepository(CreatePassingModule());
        var taskDirectory = Path.Combine(repository.RootPath, "ansight", "tasks");
        File.WriteAllText(
            Path.Combine(taskDirectory, "descriptor-tools.ts"),
            """
            // Helps explain `export const task` to workspace authors.
            export const example = "export const task = {}";
            export const taskFactory = () => ({ title: "Example" });
            """);

        var result = LoadRepository(repository.RootPath);

        Assert.Empty(result.Warnings);
        Assert.Equal("map.validate", Assert.Single(result.Tasks).TaskId);
        Assert.Equal("descriptor-tools.ts", Assert.Single(result.SupportModules).RelativePath);
    }

    [Fact]
    public void Loader_RecognizesTaskDescriptorAcrossWhitespaceAndComments()
    {
        using var repository = CreateRepository(CreatePassingModule().Replace(
            "export const task",
            "export /* descriptor */\nconst task",
            StringComparison.Ordinal));

        var result = LoadRepository(repository.RootPath);

        Assert.Empty(result.Warnings);
        Assert.Equal("map.validate", Assert.Single(result.Tasks).TaskId);
        Assert.Empty(result.SupportModules);
    }

    [Fact]
    public void Loader_StillReportsMalformedExportedTaskDescriptor()
    {
        using var repository = CreateRepository("export const task = makeTask();");

        var result = LoadRepository(repository.RootPath);

        Assert.Empty(result.Tasks);
        Assert.Empty(result.SupportModules);
        Assert.Contains("JSON object literal", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledTask_RemainsDiscoverableAndRejectsExecution()
    {
        using var repository = CreateRepository(CreatePassingModule().Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n             \"enabled\": false,",
            StringComparison.Ordinal));

        var loadResult = LoadRepository(repository.RootPath);

        Assert.Empty(loadResult.Warnings);
        var task = Assert.Single(loadResult.Tasks);
        Assert.False(task.Enabled);
        Assert.False(task.ToPublicDefinition().Enabled);

        var router = new RepositoryTaskRouter(repository.RootPath);
        var result = await router.ExecuteAsync(
            task,
            "session-1",
            new JsonObject(),
            correlationId: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Rejected, result.Status);
        Assert.Contains("disabled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Loader_IgnoresLegacyJavaScriptModuleExtensions()
    {
        using var repository = CreateRepository(CreatePassingModule());
        var taskDirectory = Path.Combine(repository.RootPath, "ansight", "tasks", "map");
        File.WriteAllText(Path.Combine(taskDirectory, "legacy.mjs"), CreatePassingModule());
        File.WriteAllText(Path.Combine(taskDirectory, "legacy.js"), CreatePassingModule());

        var result = LoadRepository(repository.RootPath);

        Assert.Empty(result.Warnings);
        Assert.Equal("map.validate", Assert.Single(result.Tasks).TaskId);
    }

    [Fact]
    public void ContractBuilder_ExtractsTheDescriptorSchemasAndRuntimeApi()
    {
        using var repository = CreateRepository(CreatePassingModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);

        var contract = RepositoryModuleContractBuilder.BuildTask(task, includeDefinitions: true);

        Assert.Equal(2, contract["contractVersion"]?.GetValue<int>());
        Assert.Equal("task", contract["moduleType"]?.GetValue<string>());
        Assert.Equal("map.validate", contract["moduleId"]?.GetValue<string>());
        Assert.Equal("Secret Garden", contract["schemas"]?["input"]?["properties"]?["label"]?["default"]?.GetValue<string>());
        Assert.Equal("object", contract["schemas"]?["output"]?["type"]?.GetValue<string>());
        Assert.Equal(
            RepositoryModuleContractArtifacts.TaskDefinitionSchemaId,
            contract["definitionSchema"]?["$id"]?.GetValue<string>());
        Assert.Equal("ansight-task.js", contract["runtimeModule"]?["fileName"]?.GetValue<string>());
        Assert.Contains("export const Permission", contract["runtimeModule"]?["source"]?.GetValue<string>(), StringComparison.Ordinal);
        var typeDefinitions = contract["typeDefinitions"]?.GetValue<string>();
        Assert.Contains("TaskFunction", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("TaskFramework", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("dotnet-maui", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("readonly ui: TaskHostUiContext", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("readonly keyboard: TaskHostKeyboardContext", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("isOpen(): Promise<KeyboardStateResult>", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("getProperties", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("getLiveNavigationStructure", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("TaskMauiContext", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("TaskReactContext", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("TaskFlutterContext", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("TaskCapacitorContext", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("readonly artifacts", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("readonly tasks: TaskHostTasksContext", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("Promise<TaskDiscoveryResult>", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("Promise<TaskCallResult<TOutput>>", typeDefinitions, StringComparison.Ordinal);
        Assert.DoesNotContain("registerApp", typeDefinitions, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnetListProjects", typeDefinitions, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_RejectsAnUnsupportedTaskSchemaVersion()
    {
        using var repository = CreateRepository(CreatePassingModule().Replace(
            "\"schemaVersion\": 1",
            "\"schemaVersion\": 3",
            StringComparison.Ordinal));

        var result = LoadRepository(repository.RootPath);

        Assert.Empty(result.Tasks);
        Assert.Contains("schemaVersion must be 1", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_RejectsAnUnknownDeclaredHostToolBeforeExecution()
    {
        using var repository = CreateRepository(CreatePassingModule().Replace(
            "\"title\": \"Validate map\",",
            "\"title\": \"Validate map\",\n             \"hostTools\": [\"ansight_not_registered\"],",
            StringComparison.Ordinal));

        var result = LoadRepository(repository.RootPath);

        Assert.Empty(result.Tasks);
        Assert.Contains("unknown or not task-callable", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void HostRegistry_ExposesOnlyToolsMarkedAsTaskSessionBound()
    {
        var registry = new OperationRegistry(
        [
            OperationRegistration.TaskSessionBound(new StubHostTool("ansight_get_app_state")),
            OperationRegistration.HostOnly(new StubHostTool("ansight_privileged_feature"))
        ]);

        var sessionTool = Assert.IsType<RepositoryTaskHostToolDescriptor>(
            registry.ResolveRepositoryTaskTool("ansight_get_app_state"));

        Assert.Equal(RepositoryTaskHostToolApplicability.SessionBound, sessionTool.Applicability);
        Assert.Null(registry.ResolveRepositoryTaskTool("ansight_privileged_feature"));
        Assert.Equal(
            "ansight_get_app_state",
            registry.BuildRepositoryTaskApiSuites()["session"]["getAppState"]);
        Assert.DoesNotContain(
            registry.BuildRepositoryTaskApiSuites().Values,
            suite => suite.Values.Contains("ansight_privileged_feature", StringComparer.Ordinal));

        using var repository = CreateRepository(CreatePassingModule().Replace(
            "\"title\": \"Validate map\",",
            "\"title\": \"Validate map\",\n             \"hostTools\": [\"ansight_privileged_feature\"],",
            StringComparison.Ordinal));
        var router = new RepositoryTaskRouter(repository.RootPath);
        router.ConfigureHostToolRegistry(registry);

        var loadResult = router.Load(repository.RootPath, "com.example.app");

        Assert.Empty(loadResult.Tasks);
        Assert.Contains("not task-callable", Assert.Single(loadResult.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void StandardAppTools_AreGroupedIntoCapabilitySuites()
    {
        var suites = RepositoryJavaScriptApiMethods.StandardAppToolSuites;

        Assert.Equal("artifacts.request", suites["artifacts"]["request"]);
        Assert.Equal("maui.get_visual_tree", suites["maui"]["getVisualTree"]);
        Assert.Equal("react.get_component_tree", suites["react"]["getComponentTree"]);
        Assert.Equal("flutter.inspect_widget", suites["flutter"]["inspectWidget"]);
        Assert.Equal("dom.query_selector", suites["capacitor"]["querySelector"]);
        Assert.DoesNotContain("dom", suites.Keys);
    }

    [Fact]
    public void LiveNavigationStructure_IsExposedThroughTheReadOnlyUiSuite()
    {
        var method = RepositoryJavaScriptApiMethods.ResolveHostApiMethod(
            "ansight_get_live_navigation_structure");

        Assert.Equal("ui", method.FeatureName);
        Assert.Equal("getLiveNavigationStructure", method.MethodName);
    }

    [Theory]
    [InlineData("ansight_open_keyboard", "open")]
    [InlineData("ansight_is_keyboard_open", "isOpen")]
    [InlineData("ansight_dismiss_keyboard", "dismiss")]
    public void KeyboardOperations_AreExposedThroughTheKeyboardSuite(
        string toolName,
        string expectedMethodName)
    {
        var method = RepositoryJavaScriptApiMethods.ResolveHostApiMethod(toolName);

        Assert.Equal("keyboard", method.FeatureName);
        Assert.Equal(expectedMethodName, method.MethodName);
    }

    [Fact]
    public void InputValidator_AppliesDefaultsAndRejectsUnknownProperties()
    {
        using var repository = CreateRepository(CreatePassingModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);

        var valid = RepositoryTaskInputValidator.TryValidateAndApplyDefaults(
            task.InputSchema,
            null,
            out var input,
            out var errorMessage);

        Assert.True(valid, errorMessage);
        Assert.Equal("Secret Garden", input["label"]?.GetValue<string>());
        Assert.Equal(400, input["durationMs"]?.GetValue<int>());

        valid = RepositoryTaskInputValidator.TryValidateAndApplyDefaults(
            task.InputSchema,
            new JsonObject { ["unexpected"] = true },
            out _,
            out errorMessage);

        Assert.False(valid);
        Assert.Contains("unknown property", errorMessage, StringComparison.OrdinalIgnoreCase);

        valid = RepositoryTaskInputValidator.TryValidateAndApplyDefaults(
            task.InputSchema,
            new JsonObject { ["durationMs"] = 2_001 },
            out _,
            out errorMessage);

        Assert.False(valid);
        Assert.Contains("at most 2000", errorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Executor_RoutesDeclaredCallsToTheEnforcedSessionAndReturnsAssertions()
    {
        using var repository = CreateRepository(CreatePassingModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        Assert.True(RepositoryTaskInputValidator.TryValidateAndApplyDefaults(
            task.InputSchema,
            null,
            out var input,
            out var validationError), validationError);
        var invocations = new List<RepositoryTaskTestInvocation>();
        var executor = CreateExecutor(
            (toolName, arguments, _) =>
            {
                invocations.Add(new RepositoryTaskTestInvocation(
                    toolName,
                    arguments.DeepClone().AsObject()));
                if (string.Equals(toolName, "ansight_find_ui", StringComparison.Ordinal))
                {
                    return Task.FromResult(RequestResult.ToolResult(
                        new JsonObject
                        {
                            ["value"] = arguments["text"]?.DeepClone()
                        },
                        isError: false));
                }

                return Task.FromResult(RequestResult.ToolResult(
                    new JsonObject
                    {
                        ["payload"] = new JsonObject
                        {
                            ["success"] = true,
                            ["result"] = new JsonObject { ["ready"] = true }
                        }
                    },
                    isError: false));
            });

        var result = await executor.ExecuteAsync(
            new RepositoryTaskExecutionRequest(
                "run-1",
                task,
                "session-1",
                input,
                "correlation-1"),
            CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
        Assert.Equal(2, result.Assertions.Count);
        Assert.All(result.Assertions, assertion => Assert.True(assertion.Passed));
        Assert.Equal(2, result.ToolCalls.Count);
        Assert.Equal(2, invocations.Count);
        Assert.All(invocations, invocation =>
            Assert.Equal("session-1", invocation.Arguments["sessionId"]?.GetValue<string>()));
        Assert.Equal(
            "redpoint.example.read",
            invocations[1].Arguments["toolId"]?.GetValue<string>());
        Assert.Null(invocations[0].Arguments["appId"]);
        Assert.Null(invocations[0].Arguments["deviceId"]);
        Assert.Null(invocations[0].Arguments["bundleIdentifier"]);
        var appArguments = Assert.IsType<JsonObject>(invocations[1].Arguments["arguments"]);
        Assert.Null(appArguments["sessionId"]);
        Assert.Null(appArguments["appId"]);
        Assert.Null(appArguments["deviceId"]);
        Assert.Null(appArguments["bundleIdentifier"]);
    }

    [Theory]
    [InlineData(
        "{\"code\":\"ambiguous_surface\",\"message\":\"Multiple surfaces match map-page.primary.\"}",
        "ambiguous_surface: Multiple surfaces match map-page.primary.")]
    [InlineData(
        "{\"error\":{\"code\":\"ambiguous_surface\",\"message\":\"Multiple surfaces match map-page.primary.\"}}",
        "ambiguous_surface: Multiple surfaces match map-page.primary.")]
    [InlineData(
        "{\"message\":\"The map surface is unavailable.\"}",
        "The map surface is unavailable.")]
    [InlineData("{\"error\":{\"code\":\"surface_not_found\"}}", "surface_not_found")]
    public async Task Executor_PreservesAppToolErrorDetailsInTheTaskResultAndCallAudit(
        string errorPayload,
        string expectedMessage)
    {
        using var repository = CreateRepository(CreatePassingModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var executor = CreateExecutor(
            (toolName, arguments, _) => Task.FromResult(toolName == "ansight_find_ui"
                ? RequestResult.ToolResult(
                    new JsonObject { ["value"] = arguments["text"]?.DeepClone() },
                    isError: false)
                : RequestResult.ToolResult(
                    new JsonObject
                    {
                        ["toolId"] = arguments["toolId"]?.DeepClone(),
                        ["responseType"] = "tool.error",
                        ["payload"] = JsonNode.Parse(errorPayload)
                    },
                    isError: true)));

        var result = await executor.ExecuteAsync(
            new RepositoryTaskExecutionRequest(
                "run-app-tool-error",
                task,
                "session-1",
                new JsonObject { ["label"] = "Secret Garden" },
                "correlation-app-tool-error"),
            CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Error, result.Status);
        Assert.Equal(expectedMessage, result.Message);
        Assert.True(Assert.Single(result.Assertions).Passed);
        var failedCall = Assert.Single(result.ToolCalls, call => call.IsError);
        Assert.Equal("redpoint.example.read", failedCall.ToolName);
        Assert.Equal(expectedMessage, failedCall.Message);
    }

    [Fact]
    public async Task Executor_SupportsThePlaywrightStyleMatcherSet()
    {
        var source = CreatePassingModule().Replace(
            "expect(found.value, { id: \"label-found\", message: \"The label must match.\" }).toEqual(input.label);",
            """
            expect({ label: found.value }, { id: "object-equal" }).toEqual({ label: input.label });
            expect([found.value], { id: "array-contains" }).toContain(input.label);
            expect([{ label: found.value }], { id: "array-contains-equal" }).toContainEqual({ label: input.label });
            expect(found.value, { id: "value-truthy" }).toBeTruthy();
            expect(false, { id: "value-falsy" }).toBeFalsy();
            expect(found.value, { id: "value-defined" }).toBeDefined();
            expect(undefined, { id: "value-undefined" }).toBeUndefined();
            expect(null, { id: "value-null" }).toBeNull();
            expect(found.value, { id: "value-not-null" }).not.toBeNull();
            """,
            StringComparison.Ordinal);
        using var repository = CreateRepository(source);
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var executor = CreateExecutor(
            (toolName, arguments, _) => Task.FromResult(RequestResult.ToolResult(
                string.Equals(toolName, "ansight_find_ui", StringComparison.Ordinal)
                    ? new JsonObject { ["value"] = arguments["text"]?.DeepClone() }
                    : new JsonObject
                    {
                        ["payload"] = new JsonObject
                        {
                            ["result"] = new JsonObject { ["ready"] = true }
                        }
                    },
                isError: false)));

        var result = await executor.ExecuteAsync(
            new RepositoryTaskExecutionRequest(
                "run-matchers",
                task,
                "session-1",
                new JsonObject { ["label"] = "Secret Garden" },
                "correlation-matchers"),
            CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
        Assert.Equal(10, result.Assertions.Count);
        Assert.All(result.Assertions, assertion => Assert.True(assertion.Passed));
    }

    [Fact]
    public async Task Executor_RoutesStandardAppMethodsByTheirRegisteredToolId()
    {
        var source = CreatePassingModule()
            .Replace("redpoint.example.read", "artifacts.request", StringComparison.Ordinal)
            .Replace("app.callTool(\"artifacts.request\",", "app.artifacts.request(", StringComparison.Ordinal);
        using var repository = CreateRepository(source);
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var dispatchedTools = new List<string>();
        var executor = CreateExecutor(
            (toolName, arguments, _) =>
            {
                dispatchedTools.Add(
                    arguments["toolId"]?.GetValue<string>()
                    ?? toolName);
                return Task.FromResult(RequestResult.ToolResult(
                    string.Equals(toolName, "ansight_find_ui", StringComparison.Ordinal)
                        ? new JsonObject { ["value"] = arguments["text"]?.DeepClone() }
                        : new JsonObject
                        {
                            ["payload"] = new JsonObject
                            {
                                ["success"] = true,
                                ["result"] = new JsonObject { ["ready"] = true }
                            }
                        },
                    isError: false));
            });

        var result = await executor.ExecuteAsync(
            new RepositoryTaskExecutionRequest(
                "run-standard-app",
                task,
                "session-standard-app",
                new JsonObject { ["label"] = "Secret Garden" },
                "correlation-standard-app"),
            CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
        Assert.Equal(["ansight_find_ui", "artifacts.request"], dispatchedTools);
    }

    [Fact]
    public async Task Executor_RoutesKeyboardMethodsToTheEnforcedSession()
    {
        using var repository = CreateRepository(CreateKeyboardModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var invocations = new List<RepositoryTaskTestInvocation>();
        var executor = CreateExecutor(
            (toolName, arguments, _) =>
            {
                invocations.Add(new RepositoryTaskTestInvocation(
                    toolName,
                    arguments.DeepClone().AsObject()));
                return Task.FromResult(RequestResult.ToolResult(
                    new JsonObject
                    {
                        ["isOpen"] = !string.Equals(
                            toolName,
                            "ansight_dismiss_keyboard",
                            StringComparison.Ordinal)
                    },
                    isError: false));
            });

        var result = await executor.ExecuteAsync(
            new RepositoryTaskExecutionRequest(
                "run-keyboard",
                task,
                "session-keyboard",
                new JsonObject(),
                "correlation-keyboard"),
            CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
        Assert.Equal(3, result.Assertions.Count);
        Assert.All(result.Assertions, assertion => Assert.True(assertion.Passed));
        Assert.Equal(
            ["ansight_open_keyboard", "ansight_is_keyboard_open", "ansight_dismiss_keyboard"],
            invocations.Select(static invocation => invocation.ToolName));
        Assert.All(invocations, invocation =>
            Assert.Equal("session-keyboard", invocation.Arguments["sessionId"]?.GetValue<string>()));
    }

    [Fact]
    public async Task Executor_RejectsAnUndeclaredToolBeforeDispatch()
    {
        using var repository = CreateRepository(
            CreatePassingModule().Replace(
                "ansight.ui.find(",
                "ansight.callTool(\"ansight_tap_ui\",",
                StringComparison.Ordinal));
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        Assert.True(RepositoryTaskInputValidator.TryValidateAndApplyDefaults(
            task.InputSchema,
            null,
            out var input,
            out var validationError), validationError);
        var dispatched = false;
        var executor = CreateExecutor(
            (_, _, _) =>
            {
                dispatched = true;
                return Task.FromResult(RequestResult.ToolResult(new JsonObject(), isError: false));
            });

        var result = await executor.ExecuteAsync(
            new RepositoryTaskExecutionRequest(
                "run-2",
                task,
                "session-1",
                input,
                "correlation-2"),
            CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Rejected, result.Status);
        Assert.False(dispatched);
        Assert.Contains("did not declare", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Executor_ReportsFailedScriptAssertionsAsAFailedTask()
    {
        using var repository = CreateRepository(
            CreatePassingModule().Replace(
                "expect(found.value, { id: \"label-found\", message: \"The label must match.\" }).toEqual(input.label);",
                "expect(found.value, { id: \"label-found\", message: \"The label must match.\" }).toEqual(\"Wrong\");",
                StringComparison.Ordinal));
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        Assert.True(RepositoryTaskInputValidator.TryValidateAndApplyDefaults(
            task.InputSchema,
            null,
            out var input,
            out var validationError), validationError);
        var executor = CreateExecutor(
            (_, arguments, _) => Task.FromResult(RequestResult.ToolResult(
                new JsonObject { ["value"] = arguments["text"]?.DeepClone() },
                isError: false)));

        var result = await executor.ExecuteAsync(
            new RepositoryTaskExecutionRequest(
                "run-3",
                task,
                "session-1",
                input,
                "correlation-3"),
            CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Failed, result.Status);
        var assertion = Assert.Single(result.Assertions);
        Assert.False(assertion.Passed);
        Assert.Equal("label-found", assertion.AssertionId);
    }

    [Fact]
    public async Task Executor_SoftFailureContinuesButStillFailsTheTask()
    {
        using var repository = CreateRepository(
            CreatePassingModule().Replace(
                "expect(found.value, { id: \"label-found\", message: \"The label must match.\" }).toEqual(input.label);",
                """
                expect.soft(found.value, { id: "label-found", message: "The label must match." }).toEqual("Wrong");
                expect(found.value, { id: "label-is-not-wrong" }).not.toBe("Wrong");
                """,
                StringComparison.Ordinal));
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        Assert.True(RepositoryTaskInputValidator.TryValidateAndApplyDefaults(
            task.InputSchema,
            null,
            out var input,
            out var validationError), validationError);
        var dispatchedTools = new List<string>();
        var executor = CreateExecutor(
            (toolName, arguments, _) =>
            {
                dispatchedTools.Add(toolName);
                return Task.FromResult(toolName == "ansight_find_ui"
                    ? RequestResult.ToolResult(
                        new JsonObject { ["value"] = arguments["text"]?.DeepClone() },
                        isError: false)
                    : RequestResult.ToolResult(
                        new JsonObject
                        {
                            ["payload"] = new JsonObject
                            {
                                ["result"] = new JsonObject { ["ready"] = true }
                            }
                        },
                        isError: false));
            });

        var result = await executor.ExecuteAsync(
            new RepositoryTaskExecutionRequest(
                "run-soft",
                task,
                "session-1",
                input,
                "correlation-soft"),
            CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Failed, result.Status);
        Assert.Equal(3, result.Assertions.Count);
        Assert.Single(result.Assertions, assertion => !assertion.Passed);
        Assert.Equal(["ansight_find_ui", "ansight_call_app_tool"], dispatchedTools);
    }

    [Fact]
    public async Task Executor_CaughtHardFailureStillFailsTheTask()
    {
        using var repository = CreateRepository(
            CreatePassingModule().Replace(
                "expect(found.value, { id: \"label-found\", message: \"The label must match.\" }).toEqual(input.label);",
                """
                try {
                  expect(found.value, { id: "label-found", message: "The label must match." }).toEqual("Wrong");
                } catch {}
                """,
                StringComparison.Ordinal));
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        Assert.True(RepositoryTaskInputValidator.TryValidateAndApplyDefaults(
            task.InputSchema,
            null,
            out var input,
            out var validationError), validationError);
        var executor = CreateExecutor(
            (toolName, arguments, _) => Task.FromResult(toolName == "ansight_find_ui"
                ? RequestResult.ToolResult(
                    new JsonObject { ["value"] = arguments["text"]?.DeepClone() },
                    isError: false)
                : RequestResult.ToolResult(
                    new JsonObject
                    {
                        ["payload"] = new JsonObject
                        {
                            ["result"] = new JsonObject { ["ready"] = true }
                        }
                    },
                    isError: false)));

        var result = await executor.ExecuteAsync(
            new RepositoryTaskExecutionRequest(
                "run-caught",
                task,
                "session-1",
                input,
                "correlation-caught"),
            CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Failed, result.Status);
        Assert.Equal(2, result.Assertions.Count);
        Assert.Single(result.Assertions, assertion => !assertion.Passed);
    }

    [Fact]
    public async Task Router_DiscoversAndRunsAnotherTaskAgainstTheEnforcedSession()
    {
        using var repository = CreateRepository(CreateComposingModule());
        var taskDirectory = Path.Combine(repository.RootPath, "ansight", "tasks", "map");
        File.WriteAllText(Path.Combine(taskDirectory, "child.ts"), CreateChildModule());
        var router = CreateConfiguredRouter(repository.RootPath);
        var loadResult = router.Load(repository.RootPath, "com.example.app");
        Assert.Empty(loadResult.Warnings);
        var task = Assert.Single(loadResult.Tasks, candidate => candidate.TaskId == "map.validate");
        Assert.True(RepositoryTaskInputValidator.TryValidateAndApplyDefaults(
            task.InputSchema,
            null,
            out var input,
            out var validationError), validationError);

        var result = await router.ExecuteAsync(
            task,
            "session-composition",
            input,
            "correlation-composition",
            CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
        Assert.Equal(3, result.Assertions.Count);
        Assert.All(result.Assertions, assertion => Assert.True(assertion.Passed));
        Assert.Equal(
            ["ansight.tasks.list", "ansight.tasks.run"],
            result.ToolCalls.Select(static call => call.ToolName));
        Assert.All(result.ToolCalls, call => Assert.False(call.IsError));
        Assert.Equal("session-composition", result.Output?["sessionId"]?.GetValue<string>());
        Assert.Equal("nested-value", result.Output?["value"]?.GetValue<string>());
    }

    [Fact]
    public async Task Router_RejectsRecursiveTaskCalls()
    {
        const string source = """
                              export const task = {
                                "schemaVersion": 1,
                                "appId": "com.example.app",
                                "title": "Recursive task",
                                "description": "Attempts to call itself.",
                                "maximumActions": 1
                              };

                              export default async function run({ ansight, expect }) {
                                await ansight.tasks.run({ taskId: "map.validate" });
                                expect(true, { id: "unreachable" }).toBe(true);
                              }
                              """;
        using var repository = CreateRepository(source);
        var router = CreateConfiguredRouter(repository.RootPath);
        var task = Assert.Single(router.Load(repository.RootPath, "com.example.app").Tasks);

        var result = await router.ExecuteAsync(
            task,
            "session-recursion",
            new JsonObject(),
            correlationId: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Error, result.Status);
        Assert.Contains("Recursive repository task call rejected", result.Message, StringComparison.Ordinal);
        var toolCall = Assert.Single(result.ToolCalls);
        Assert.Equal("ansight.tasks.run", toolCall.ToolName);
        Assert.True(toolCall.IsError);
    }

    [Fact]
    public async Task Router_PropagatesAChildFailureToTheCallingTask()
    {
        using var repository = CreateRepository(CreateComposingModule());
        var taskDirectory = Path.Combine(repository.RootPath, "ansight", "tasks", "map");
        File.WriteAllText(
            Path.Combine(taskDirectory, "child.ts"),
            CreateChildModule().Replace(
                "expect(input.value, { id: \"child-value\" }).toBe(\"nested-value\");",
                "expect(input.value, { id: \"child-value\" }).toBe(\"different-value\");",
                StringComparison.Ordinal));
        var router = CreateConfiguredRouter(repository.RootPath);
        var task = Assert.Single(
            router.Load(repository.RootPath, "com.example.app").Tasks,
            candidate => candidate.TaskId == "map.validate");

        var result = await router.ExecuteAsync(
            task,
            "session-child-failure",
            new JsonObject(),
            correlationId: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Error, result.Status);
        Assert.Contains("Expected values to be identical", result.Message, StringComparison.Ordinal);
        Assert.Equal(2, result.ToolCalls.Count);
        Assert.Equal("ansight.tasks.run", result.ToolCalls[1].ToolName);
        Assert.True(result.ToolCalls[1].IsError);
    }

    [Fact]
    public void TaskApiSurface_RecognizesTaskCompositionMethods()
    {
        const string source = """
                              const catalog = await ansight.tasks.list({ query: "map" });
                              await ansight.tasks.run({ taskId: catalog.tasks[0].taskId, input: {} });
                              """;

        Assert.Empty(LocalTaskExtractionCoordinator.ValidateTaskApiSurface(source));
    }

    private static TemporaryDirectory CreateRepository(string source)
    {
        var repository = new TemporaryDirectory();
        var taskDirectory = Path.Combine(repository.RootPath, "ansight", "tasks", "map");
        Directory.CreateDirectory(taskDirectory);
        File.WriteAllText(Path.Combine(taskDirectory, "validate.ts"), source);
        return repository;
    }

    private static RepositoryTaskLoadResult LoadRepository(string repositoryRootPath)
        => RepositoryTaskLoader.Load(repositoryRootPath, "com.example.app", ResolveHostTool);

    private static JavaScriptRepositoryTaskExecutor CreateExecutor(RepositoryTaskToolExecutor toolExecutor)
        => new(
            "node",
            toolExecutor,
            hostApiSuites: new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["keyboard"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["open"] = "ansight_open_keyboard",
                    ["isOpen"] = "ansight_is_keyboard_open",
                    ["dismiss"] = "ansight_dismiss_keyboard"
                },
                ["ui"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["find"] = "ansight_find_ui",
                    ["tap"] = "ansight_tap_ui"
                }
            });

    private static RepositoryTaskRouter CreateConfiguredRouter(string applicationDataPath)
    {
        var router = new RepositoryTaskRouter(applicationDataPath);
        router.ConfigureHostToolRegistry(new OperationRegistry([]));
        router.ConfigureToolExecutor((_, _, _) => Task.FromResult(
            RequestResult.ToolResult(new JsonObject(), isError: false)));
        return router;
    }

    private static RepositoryTaskHostToolDescriptor? ResolveHostTool(string toolName)
        => toolName switch
        {
            "ansight_find_ui" => new RepositoryTaskHostToolDescriptor(
                toolName,
                "ui",
                "find",
                RepositoryTaskHostToolApplicability.SessionBound),
            "ansight_tap_ui" => new RepositoryTaskHostToolDescriptor(
                toolName,
                "ui",
                "tap",
                RepositoryTaskHostToolApplicability.SessionBound),
            _ => null
        };

    private static string CreatePassingModule()
        => """
           export const task = {
             "schemaVersion": 1,
             "appId": "com.example.app",
             "title": "Validate map",
             "description": "Validates a named map item.",
             "feature": "map",
             "keywords": ["area", "annotation"],
             "inputSchema": {
               "type": "object",
               "properties": {
                 "label": { "type": "string", "default": "Secret Garden" },
                 "durationMs": {
                   "type": "integer",
                   "minimum": 50,
                   "maximum": 2000,
                   "default": 400
                 }
               },
               "additionalProperties": false
             },
             "outputSchema": {
               "type": "object",
               "properties": {
                 "label": { "type": "string" }
               },
               "required": ["label"],
               "additionalProperties": false
             },
             "timeoutSeconds": 10,
             "maximumActions": 4
           };

           export default async function run({ input, ansight, app, expect }) {
             const found = await ansight.ui.find({
               text: input.label,
               sessionId: "session-override",
               appId: "app-override",
               deviceId: "device-override",
               bundleIdentifier: "bundle-override"
             });
             expect(found.value, { id: "label-found", message: "The label must match." }).toEqual(input.label);
             const appResult = await app.callTool("redpoint.example.read", {
               sessionId: "session-override",
               appId: "app-override",
               deviceId: "device-override",
               bundleIdentifier: "bundle-override"
             });
             expect(appResult.payload.result.ready, { id: "app-ready", message: "The app must be ready." }).toBe(true);
             return { label: input.label };
           }
           """;

    private static string CreateKeyboardModule()
        => """
           export const task = {
             "schemaVersion": 1,
             "appId": "com.example.app",
             "title": "Exercise the software keyboard",
             "description": "Opens, inspects, and dismisses the software keyboard.",
             "inputSchema": {
               "type": "object",
               "properties": {},
               "additionalProperties": false
             },
             "timeoutSeconds": 10,
             "maximumActions": 3
           };

           export default async function run({ ansight, expect }) {
             const opened = await ansight.keyboard.open({
               automationId: "search-field",
               role: "textbox"
             });
             expect(opened.isOpen, { id: "keyboard-opened" }).toBe(true);
             const state = await ansight.keyboard.isOpen();
             expect(state.isOpen, { id: "keyboard-is-open" }).toBe(true);
             const dismissed = await ansight.keyboard.dismiss({ includeScreenshot: false });
             expect(dismissed.isOpen, { id: "keyboard-dismissed" }).toBe(false);
           }
           """;

    private static string CreateComposingModule()
        => """
           export const task = {
             "schemaVersion": 1,
             "appId": "com.example.app",
             "title": "Compose map workflow",
             "description": "Discovers and runs the nested map worker.",
             "timeoutSeconds": 10,
             "maximumActions": 2
           };

           export default async function run({ ansight, expect }) {
             const catalog = await ansight.tasks.list({ query: "nested worker" });
             expect(catalog.tasks.map(task => task.taskId), { id: "child-discovered" })
               .toContain("map.child");
             const child = await ansight.tasks.run({
               taskId: "map.child",
               input: { value: "nested-value" }
             });
             expect(child.sessionId, { id: "child-session-pinned" }).toBe("session-composition");
             expect(child.output.value, { id: "child-output" }).toBe("nested-value");
             return {
               sessionId: child.sessionId,
               value: child.output.value
             };
           }
           """;

    private static string CreateChildModule()
        => """
           export const task = {
             "schemaVersion": 1,
             "appId": "com.example.app",
             "title": "Nested map worker",
             "description": "Returns a validated value from a nested task call.",
             "feature": "map",
             "keywords": ["nested", "worker"],
             "inputSchema": {
               "type": "object",
               "properties": {
                 "value": { "type": "string" }
               },
               "required": ["value"],
               "additionalProperties": false
             },
             "timeoutSeconds": 10,
             "maximumActions": 1
           };

           export default async function run({ run, input, expect }) {
             expect(input.value, { id: "child-value" }).toBe("nested-value");
             return {
               sessionId: run.sessionId,
               value: input.value
             };
           }
           """;

    private sealed record RepositoryTaskTestInvocation(
        string ToolName,
        JsonObject Arguments);

    private sealed class StubHostTool(string name) : IOperation
    {
        public string Name { get; } = name;

        public JsonObject Definition { get; } = new()
        {
            ["name"] = name,
            ["description"] = "Test tool.",
            ["inputSchema"] = new JsonObject { ["type"] = "object" }
        };

        public Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
            => Task.FromResult(RequestResult.ToolResult(new JsonObject(), isError: false));
    }
}
