using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Replay;
using Ansight.Host.Runtime.Automation;
using Ansight.Host.Runtime.RepositoryContracts;

namespace Ansight.Host.Tests.Unit.Replay;

public sealed class LocalTaskExtractionCoordinatorTests
{
    [Fact]
    public void ValidateStartRequest_RequiresPositiveSelectedPeriod()
    {
        var timestamp = DateTimeOffset.UtcNow;

        var exception = Assert.Throws<InvalidDataException>(() =>
            LocalTaskExtractionCoordinator.ValidateStartRequest(new LocalTaskExtractionStartRequest(
                "session-1",
                timestamp,
                timestamp,
                "Extract the captured checkout workflow.")));

        Assert.Contains("positive duration", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateStartRequest_RequiresAgentHandoffDescription(string description)
    {
        var timestamp = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentException>(() =>
            LocalTaskExtractionCoordinator.ValidateStartRequest(new LocalTaskExtractionStartRequest(
                "session-1",
                timestamp,
                timestamp.AddSeconds(5),
                description)));
    }

    [Theory]
    [InlineData(null, LocalTaskExtractionModes.Hosted)]
    [InlineData("hosted", LocalTaskExtractionModes.Hosted)]
    [InlineData("websocket", LocalTaskExtractionModes.DirectWebSocket)]
    [InlineData("direct", LocalTaskExtractionModes.DirectWebSocket)]
    [InlineData("directWebSocket", LocalTaskExtractionModes.DirectWebSocket)]
    public void NormalizeMode_RecognizesHostedAndDirectWebSocketAliases(
        string? value,
        string expected)
    {
        Assert.Equal(expected, LocalTaskExtractionCoordinator.NormalizeMode(value));
    }

    [Fact]
    public void StartRequest_DefaultsSelectorEvidenceValidationOn()
    {
        var request = JsonSerializer.Deserialize<LocalTaskExtractionStartRequest>("""
            {
              "sessionId": "session-1",
              "startUtc": "2026-09-05T00:00:00Z",
              "endUtc": "2026-09-05T00:00:05Z",
              "description": "Extract the recorded UI flow."
            }
            """);

        Assert.NotNull(request);
        Assert.True(request.ValidateSelectors);
        Assert.Equal("fast", request.Reasoning);
        Assert.Equal(string.Empty, request.Model);
    }

    [Fact]
    public void StartRequest_DeserializesSharedReasoningWithoutClientModelSelection()
    {
        var request = JsonSerializer.Deserialize<LocalTaskExtractionStartRequest>("""
            {"sessionId":"session-1","startUtc":"2026-09-05T00:00:00Z",
             "endUtc":"2026-09-05T00:00:05Z","description":"Extract checkout.","reasoning":"deep"}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        LocalTaskExtractionCoordinator.ValidateStartRequest(request);
        Assert.Equal("deep", request.Reasoning);
        Assert.Equal(string.Empty, request.Model);
    }

    [Fact]
    public void StartRequest_RejectsUnknownReasoning()
    {
        var request = new LocalTaskExtractionStartRequest(
            "session-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(5), "Extract checkout.")
        {
            Reasoning = "best"
        };
        Assert.Throws<ArgumentException>(() => LocalTaskExtractionCoordinator.ValidateStartRequest(request));
    }

    [Fact]
    public void ResolveTaskName_UsesExplicitNameWhenProvided()
    {
        var request = new LocalTaskExtractionStartRequest(
            "session-1",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddSeconds(5),
            "Extract the recorded UI flow.",
            TaskName: "  Load area weather  ");

        Assert.Equal("Load area weather", LocalTaskExtractionCoordinator.ResolveTaskName(request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveTaskName_DerivesNameFromDescriptionWhenTaskNameIsBlank(string? taskName)
    {
        var request = new LocalTaskExtractionStartRequest(
            "session-1",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddSeconds(5),
            "  Extract the recorded UI flow.  ",
            TaskName: taskName);

        LocalTaskExtractionCoordinator.ValidateStartRequest(request);

        Assert.Equal(
            "Extract the recorded UI flow.",
            LocalTaskExtractionCoordinator.ResolveTaskName(request));
    }

    [Fact]
    public void ValidateStartRequest_RejectsTaskNamesOverMaximumLength()
    {
        var request = new LocalTaskExtractionStartRequest(
            "session-1",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddSeconds(5),
            "Extract the recorded UI flow.",
            TaskName: new string('a', 201));

        var exception = Assert.Throws<InvalidDataException>(() =>
            LocalTaskExtractionCoordinator.ValidateStartRequest(request));

        Assert.Contains("200 characters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAuthoringReferences_ProjectsToolsProvidersAndArtifacts()
    {
        var catalog = new SessionAppToolCatalogSnapshot(
            "ansight.session-app-tool-catalog/v1",
            DateTimeOffset.Parse("2026-09-06T01:00:00Z"),
            new JsonObject
            {
                ["tools"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = "map.query_state",
                        ["title"] = "Query map state",
                        ["description"] = "Reads the current map state.",
                        ["policy"] = "read",
                        ["executable"] = true
                    },
                    new JsonObject
                    {
                        ["id"] = "map.reset",
                        ["executable"] = false
                    })
            },
            new JsonObject
            {
                ["result"] = new JsonObject
                {
                    ["providers"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "map.exports",
                        ["name"] = "Map exports",
                        ["artifacts"] = new JsonArray(new JsonObject
                        {
                            ["id"] = "offline-area",
                            ["name"] = "Offline area"
                        })
                    })
                }
            });

        var references = LocalTaskExtractionCoordinator.BuildAuthoringReferences(catalog);

        Assert.Contains(references, reference => reference.Mention == "@tool:map.query_state");
        Assert.DoesNotContain(references, reference => reference.Mention == "@tool:map.reset");
        Assert.Contains(references, reference => reference.Mention == "@artifact-provider:map.exports");
        Assert.Contains(references, reference => reference.Mention == "@artifact:map.exports/offline-area");
    }

    [Fact]
    public void ReferencedCapabilityHandoff_IncludesOnlySelectedToolDefinitions()
    {
        var catalog = new SessionAppToolCatalogSnapshot(
            "ansight.session-app-tool-catalog/v1",
            DateTimeOffset.Parse("2026-09-06T01:00:00Z"),
            new JsonObject
            {
                ["tools"] = new JsonArray(
                    new JsonObject { ["id"] = "map.query_state", ["description"] = "Selected" },
                    new JsonObject { ["id"] = "map.reset", ["description"] = "Not selected" })
            },
            null);

        var handoff = LocalTaskExtractionCoordinator.BuildReferencedAppCapabilityHandoff(
            "Use @tool:map.query_state to inspect the focused area.",
            catalog);

        Assert.Contains("map.query_state", handoff, StringComparison.Ordinal);
        Assert.DoesNotContain("map.reset", handoff, StringComparison.Ordinal);
    }

    [Fact]
    public void ReferencedCapabilityHandoff_DoesNotSelectAPrefixToolId()
    {
        var catalog = new SessionAppToolCatalogSnapshot(
            "ansight.session-app-tool-catalog/v1",
            DateTimeOffset.Parse("2026-09-06T01:00:00Z"),
            new JsonObject
            {
                ["tools"] = new JsonArray(
                    new JsonObject { ["id"] = "map.query" },
                    new JsonObject { ["id"] = "map.query_state" })
            },
            null);

        var handoff = LocalTaskExtractionCoordinator.BuildReferencedAppCapabilityHandoff(
            "Use @tool:map.query_state.",
            catalog);

        Assert.Contains("map.query_state", handoff, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\":\"map.query\"", handoff, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OpenAI request failed.")]
    [InlineData("The brokered connection is unavailable.")]
    [InlineData("No credential was returned.")]
    [InlineData("The HTTP proxy rejected the request.")]
    [InlineData("The WebSocket closed unexpectedly.")]
    public void SanitizeUserFacingMessage_HidesImplementationDetails(string message)
    {
        const string fallback = "Task extraction failed. Please try again.";

        Assert.Equal(
            fallback,
            LocalTaskExtractionCoordinator.SanitizeUserFacingMessage(message, fallback));
    }

    [Fact]
    public void SanitizeUserFacingMessage_PreservesProductLevelGuidance()
    {
        const string message = "Your account needs access to task extraction.";

        Assert.Equal(
            message,
            LocalTaskExtractionCoordinator.SanitizeUserFacingMessage(
                message,
                "Task extraction failed. Please try again."));
    }

    [Fact]
    public void Slugify_ProducesSafeRepositoryTaskFileName()
    {
        Assert.Equal(
            "create-customer-verify-search",
            LocalTaskExtractionCoordinator.Slugify(" Create customer & verify search! "));
    }

    [Fact]
    public void NormalizeTaskDescriptorSource_QuotesTypeScriptDescriptorPropertyNames()
    {
        const string source = """
                              import type { TaskDefinition, TaskInvocation } from "./ansight-task.d.ts";

                              export const task = {
                                schemaVersion: 1,
                                appId: "com.example.app",
                                title: "Open account",
                                description: "Opens the account page.",
                                inputSchema: {
                                  type: "object",
                                  properties: {},
                                  additionalProperties: false
                                }
                              } satisfies TaskDefinition;

                              export default async function runTask({ expect }: TaskInvocation) {
                                expect(true, { id: "account-open", message: "The account page is open." }).toBe(true);
                              }
                              """;

        var normalized = LocalTaskExtractionCoordinator.NormalizeTaskDescriptorSource(source);

        Assert.Contains("\"schemaVersion\": 1", normalized, StringComparison.Ordinal);
        Assert.Contains("\"inputSchema\": {", normalized, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\": false", normalized, StringComparison.Ordinal);
        Assert.Contains("function runTask({ expect }", normalized, StringComparison.Ordinal);
        var descriptor = RepositoryModuleDescriptorReader.ExtractObject(
            normalized,
            "draft.ts",
            "task",
            "Task");
        using var document = JsonDocument.Parse(descriptor);
        Assert.Equal("Open account", document.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void NormalizeTaskDescriptorSource_PreservesAlreadyJsonCompatibleDescriptor()
    {
        const string source = """
                              export const task = {
                                "schemaVersion": 1,
                                "title": "Keep { nested: text } unchanged",
                                "description": "A task"
                              };
                              """;

        Assert.Equal(source, LocalTaskExtractionCoordinator.NormalizeTaskDescriptorSource(source));
    }

    [Fact]
    public void ValidateTaskActionBudget_RejectsBudgetBelowStaticActionCallCount()
    {
        const string source = """
                              export const task = {
                                "title": "Load weather",
                                "description": "Loads weather for an area.",
                                "maximumActions": 4
                              };

                              await ansight.ui.tap({ text: "Areas" });
                              await ansight.ui.tap({ text: "Kalymnos" });
                              await ansight.ui.swipe({ direction: "up" });
                              await ansight.ui.waitFor({ text: "Weather" });
                              await app.callTool("weather.status", {});
                              """;

        var errors = LocalTaskExtractionCoordinator.ValidateTaskActionBudget(source);

        var error = Assert.Single(errors);
        Assert.Contains("action budget of 4", error, StringComparison.Ordinal);
        Assert.Contains("at least 5", error, StringComparison.Ordinal);
        Assert.Contains("64-action default", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateTaskActionBudget_UsesDefaultWhenLimitIsOmitted()
    {
        const string source = """
                              export const task = {
                                "title": "Load weather",
                                "description": "Loads weather for an area."
                              };

                              // await ansight.ui.tap({ text: "This comment does not count" });
                              const example = "await app.callTool('this.string.does.not.count', {});";
                              await ansight.ui.tap({ text: "Areas" });
                              await ansight.ui.waitFor({ text: "Weather" });
                              """;

        Assert.Empty(LocalTaskExtractionCoordinator.ValidateTaskActionBudget(source));
    }

    [Fact]
    public void ValidateTaskApiSurface_RejectsInventedTopLevelAnsightMethods()
    {
        const string source = """
                              await ansight.tap({ automationId: "account" });
                              await ansight.waitFor({ automationId: "AccountPage" });
                              const page = await ansight.getCurrentPage();
                              """;

        var errors = LocalTaskExtractionCoordinator.ValidateTaskApiSurface(source);

        Assert.Contains(errors, error => error.Contains("ansight.ui.tap", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("ansight.ui.waitFor", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("app.maui.getCurrentPage", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateTaskApiSurface_AcceptsBundledFeatureSlicedMethods()
    {
        const string source = """
                              const page = await ansight.ui.find({ automationId: "AccountPage" });
                              await ansight.ui.tap({ automationId: "account" });
                              const result = await app.maui.getCurrentPage();
                              expect(page.selectorSatisfied, { id: "account-page-visible" }).toBeTruthy();
                              expect(result.isSuccess, { id: "framework-inspection-succeeded" }).toBe(true);
                              """;

        Assert.Empty(LocalTaskExtractionCoordinator.ValidateTaskApiSurface(source));
    }

    [Fact]
    public void ValidateTaskApiSurface_AcceptsKeyboardMethods()
    {
        const string source = """
                              const before = await ansight.keyboard.isOpen();
                              await ansight.keyboard.open({ automationId: "search", role: "textbox" });
                              await ansight.keyboard.dismiss();
                              expect(before.isOpen, { id: "keyboard-state" }).toBe(false);
                              """;

        Assert.Empty(LocalTaskExtractionCoordinator.ValidateTaskApiSurface(source));
    }

    [Fact]
    public void ValidateTaskApiSurface_RejectsLegacyCheckAssertions()
    {
        const string source = "check.ok(true, \"legacy-check\");";

        var error = Assert.Single(LocalTaskExtractionCoordinator.ValidateTaskApiSurface(source));

        Assert.Contains("expect(actual, metadata)", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateTaskApiSurface_IgnoresExamplesInsideCommentsAndStrings()
    {
        const string source = """
                              // Never call ansight.tap({}).
                              const message = "ansight.getCurrentPage() is not supported";
                              await ansight.ui.tap({ automationId: "account" });
                              expect(message.length > 0, { id: "message-created" }).toBeTruthy();
                              """;

        Assert.Empty(LocalTaskExtractionCoordinator.ValidateTaskApiSurface(source));
    }

    [Fact]
    public void LocalTypeScriptTaskCompiler_CompilesAgainstBundledContractAndReportsSemanticErrors()
    {
        if (LocalTypeScriptTaskCompiler.ResolveCompilerPath() is null)
        {
            return;
        }

        var taskDirectory = Path.Combine(
            Path.GetTempPath(),
            "ansight-typescript-task-compiler-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(taskDirectory);
        try
        {
            File.WriteAllText(
                Path.Combine(taskDirectory, "selectors.ts"),
                "export function accountPageSelector() { return { automationId: \"AccountPage\" }; }");
            var sourcePath = Path.Combine(taskDirectory, "account-page.ts");
            File.WriteAllText(sourcePath, """
                                          import type { TaskDefinition, TaskInvocation } from "./ansight-task.d.ts";
                                          import { accountPageSelector } from "./selectors.ts";

                                          export const task = {
                                            "schemaVersion": 1,
                                            "title": "Account page",
                                            "description": "Verifies the account page.",
                                            "inputSchema": {
                                              "type": "object",
                                              "properties": {},
                                              "additionalProperties": false
                                            }
                                          } satisfies TaskDefinition;

                                          export default async function runTask({ ansight, expect }: TaskInvocation) {
                                            const page = await ansight.ui.find(accountPageSelector());
                                            expect(page.selectorSatisfied, { id: "account-page-visible" }).toBeTruthy();
                                          }
                                          """);

            Assert.Empty(LocalTypeScriptTaskCompiler.Validate(
                taskDirectory,
                sourcePath,
                RepositoryModuleContractArtifacts.GetTaskTypeDefinitions()));

            File.WriteAllText(sourcePath, """
                                          import type { TaskDefinition, TaskInvocation } from "./ansight-task.d.ts";

                                          export const task = {
                                            "schemaVersion": 1,
                                            "title": "Account page",
                                            "description": "Verifies the account page.",
                                            "inputSchema": {
                                              "type": "object",
                                              "properties": {},
                                              "additionalProperties": false
                                            }
                                          } satisfies TaskDefinition;

                                          export default async function runTask({ ansight }: TaskInvocation) {
                                            const page = await ansight.ui.find({ automationId: "AccountPage" });
                                            const page = await ansight.ui.find({ automationId: "AccountPage" });
                                            return page.automationId;
                                          }
                                          """);

            var errors = LocalTypeScriptTaskCompiler.Validate(
                taskDirectory,
                sourcePath,
                RepositoryModuleContractArtifacts.GetTaskTypeDefinitions());

            Assert.Contains(errors, error => error.Contains("Cannot redeclare block-scoped variable 'page'", StringComparison.Ordinal));
            Assert.Contains(errors, error => error.Contains("Property 'automationId' does not exist", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public void LocalTypeScriptTaskCompiler_UsesExplicitNodeRuntimeForUnixCompilerScript()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var startInfo = LocalTypeScriptTaskCompiler.CreateStartInfo(
            "/opt/homebrew/bin/tsc",
            "/tmp/task/tsconfig.extraction.json",
            "/opt/homebrew/bin/node");

        Assert.Equal("/opt/homebrew/bin/node", startInfo.FileName);
        Assert.Equal("/opt/homebrew/bin/tsc", startInfo.ArgumentList[0]);
        Assert.Contains("/tmp/task/tsconfig.extraction.json", startInfo.ArgumentList);
    }

    [Fact]
    public void LocalTypeScriptTaskCompiler_ReportsMissingNodeAsInfrastructureFailure()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var compilerPath = LocalTypeScriptTaskCompiler.ResolveCompilerPath();
        if (compilerPath is null)
        {
            return;
        }

        var missingRuntimePath = Path.Combine(
            Path.GetTempPath(),
            "ansight-missing-node",
            Guid.NewGuid().ToString("N"));

        Assert.Equal(
            LocalTypeScriptTaskCompiler.MissingRuntimeMessage,
            LocalTypeScriptTaskCompiler.GetEnvironmentError(
                missingRuntimePath,
                configuredCompilerPath: compilerPath));

        var validation = LocalTypeScriptTaskCompiler.ValidateDetailed(
            Path.Combine(Path.GetTempPath(), "ansight-missing-node-validation"),
            Path.Combine(Path.GetTempPath(), "ansight-missing-node-validation", "task.ts"),
            "export {};",
            missingRuntimePath);

        Assert.True(validation.IsInfrastructureFailure);
        Assert.Equal(LocalTypeScriptTaskCompiler.MissingRuntimeMessage, Assert.Single(validation.Diagnostics));
    }
}
