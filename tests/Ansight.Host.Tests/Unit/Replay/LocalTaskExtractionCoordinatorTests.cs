using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Replay;
using Ansight.Host.Runtime.Automation;
using Ansight.Host.Runtime.RepositoryContracts;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Replay;

public sealed class LocalTaskExtractionCoordinatorTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(11, true)]
    [InlineData(12, true)]
    public void BuildAgentToolsForTurn_ReservesFinalPassesForDraftSubmission(int turn, bool submissionOnly)
    {
        var tools = LocalTaskExtractionCoordinator.BuildAgentToolsForTurn(turn)
            .OfType<JsonObject>()
            .Select(tool => tool["name"]!.GetValue<string>())
            .ToArray();

        Assert.Contains("submit_task_draft", tools);
        if (submissionOnly)
        {
            Assert.Single(tools);
        }
        else
        {
            Assert.Contains("inspect_raw_visual_tree", tools);
            Assert.Contains("inspect_task_contract", tools);
        }
    }

    [Fact]
    public void BuildRecoveryDraft_ReturnsRecordedSourceWhenAgentNeverSubmits()
    {
        const string reason = "The extraction agent reached its 12-pass limit without completing a draft.";
        var seed = new TimelineTaskExtraction(
            "copy-location", "// Recorded location-sharing steps", 3, 2, ["Copy target needs review."]);
        var baseFolder = Path.GetTempPath();

        var draft = LocalTaskExtractionCoordinator.BuildRecoveryDraft(baseFolder, "extraction-1", seed, null, reason);

        Assert.Equal(seed.Source, draft.Source);
        Assert.Equal(seed.Summary, draft.Summary);
        Assert.Equal("copy-location", draft.SuggestedName);
        Assert.Equal(Path.Combine(baseFolder, "task-extraction-drafts", "extraction-1"), draft.DraftRootPath);
        Assert.Equal(Path.Combine(draft.DraftRootPath, RepositoryTaskLoader.TaskDirectoryRelativePath, "copy-location.ts"), draft.SourcePath);
        Assert.Contains("Copy target needs review.", draft.ValidationWarnings);
        Assert.Contains(reason, draft.ValidationWarnings);
        Assert.Contains(draft.ValidationWarnings, warning => warning.Contains("product-outcome assertions", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildRecoveryDraft_PreservesSubmittedSourceAndDiagnosticsAfterLaterFailure()
    {
        var seed = new TimelineTaskExtraction("recorded-seed", "// Original seed", 1, 1, []);
        var submitted = new LocalTaskExtractionDraft(
            "copy-location", "Copy and verify GPS", "// Agent-authored clipboard assertion", "copy-location",
            Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "copy-location.ts"), ["Selector requires review."]);
        const string reason = "The model connection ended during a revision.";

        var draft = LocalTaskExtractionCoordinator.BuildRecoveryDraft(Path.GetTempPath(), "extraction-1", seed, submitted, reason);

        Assert.Equal(submitted.Source, draft.Source);
        Assert.Equal(submitted.SourcePath, draft.SourcePath);
        Assert.Equal(submitted.TaskId, draft.TaskId);
        Assert.Equal(submitted.Summary, draft.Summary);
        Assert.Equal(new[] { "Selector requires review.", reason }, draft.ValidationWarnings);
        Assert.Single(submitted.ValidationWarnings);
    }

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
        Assert.True(request.TrimToTechnology);
        Assert.True(request.IncludeOnlyNecessaryFeatures);
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
    public void ResolveTaskName_BoundsLongAnnotationWithoutChangingHandoff()
    {
        const string description = """
            In this section, the tester searched Eagle Rock for "Approach", opened location sharing, and copied its GPS location to the clipboard while the approach map remained visible.
            1. Completed the search text "Approach"; About showed one match with the Location map and elevation profile.
            2. Tapped Share beside Location, opening options for Location (GPS) and Location (Google Maps).
            3. Tapped the copy control for Location (GPS); the Share sheet closed.
            4. Saw "Copied the location for 'Eagle Rock' to your clipboard." while the Location map remained open.
            To validate, use the clipboard tool to check the contents against the displayed GPS location.
            """;
        var request = new LocalTaskExtractionStartRequest(
            "session-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(5), description);

        LocalTaskExtractionCoordinator.ValidateStartRequest(request);
        var title = LocalTaskExtractionCoordinator.ResolveTaskName(request);

        Assert.True(description.Length > 500);
        Assert.InRange(title.Length, 1, 60);
        Assert.Equal("In this section, the tester searched Eagle Rock for…", title);
        Assert.Equal(description, request.Description);
        LocalTaskExtractionCoordinator.ValidateStartRequest(request with { TaskName = title });
    }

    [Theory]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(500)]
    [InlineData(501)]
    [InlineData(8_000)]
    public void ResolveTaskName_BoundsDescriptionsWithoutWordBreaks(int length)
    {
        var request = new LocalTaskExtractionStartRequest(
            "session-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(5), new string('a', length));

        LocalTaskExtractionCoordinator.ValidateStartRequest(request);
        var title = LocalTaskExtractionCoordinator.ResolveTaskName(request);

        Assert.Equal(length <= 60 ? request.Description : new string('a', 59) + "…", title);
    }

    [Theory]
    [InlineData("  Copy\r\nEagle Rock\t GPS  ", "Copy Eagle Rock GPS")]
    [InlineData("📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍", "📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍📍…")]
    public void ResolveTaskName_NormalizesWhitespaceAndPreservesUnicode(string description, string expected)
    {
        var request = new LocalTaskExtractionStartRequest(
            "session-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(5), description);

        Assert.Equal(expected, LocalTaskExtractionCoordinator.ResolveTaskName(request));
    }

    [Fact]
    public void ResolveTaskName_PreservesExplicitNameAtMaximumLength()
    {
        var name = new string('a', 200);
        var request = new LocalTaskExtractionStartRequest(
            "session-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(5),
            "Extract the recorded UI flow.", TaskName: $"  {name}  ");

        LocalTaskExtractionCoordinator.ValidateStartRequest(request);

        Assert.Equal(name, LocalTaskExtractionCoordinator.ResolveTaskName(request));
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

    [Fact]
    public void CompactTypeDefinitions_PreservesDeclarationsAndLiteralCommentMarkers()
    {
        const string source = """
                              /** Documentation is not needed by the extraction agent. */
                              export type Route = `${string}.${string}`;
                              export type Url = "https://example.com/a/*/b";
                              // Another comment.
                              export interface TaskDefinition { title: string; }
                              """;

        var compact = LocalTaskExtractionCoordinator.CompactTypeDefinitions(source);

        Assert.Contains("export type Route = `${string}.${string}`;", compact, StringComparison.Ordinal);
        Assert.Contains("https://example.com/a/*/b", compact, StringComparison.Ordinal);
        Assert.Contains("export interface TaskDefinition { title: string; }", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("Documentation is not needed", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("Another comment", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactTypeDefinitions_SubstantiallyReducesBundledContract()
    {
        var full = RepositoryModuleContractArtifacts.GetTaskTypeDefinitions();
        var compact = LocalTaskExtractionCoordinator.CompactTypeDefinitions(full);

        Assert.True(compact.Length < full.Length / 3);
        Assert.Contains("export interface TaskDefinition", compact, StringComparison.Ordinal);
        Assert.Contains("export interface TaskInvocation", compact, StringComparison.Ordinal);
        Assert.Contains("export type AndroidPermission", compact, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("react-native", "TaskReactContext", "readonly react: TaskReactContext;")]
    [InlineData("flutter", "TaskFlutterContext", "readonly flutter: TaskFlutterContext;")]
    [InlineData("dotnet-maui", "TaskMauiContext", "readonly maui: TaskMauiContext;")]
    public void TrimTypeDefinitionsForFramework_RetainsOnlySupportedFrameworkSuite(
        string framework,
        string selectedInterface,
        string selectedProperty)
    {
        var full = RepositoryModuleContractArtifacts.GetTaskTypeDefinitions();
        var selected = LocalTaskExtractionCoordinator.TrimTypeDefinitionsForFramework(full, framework);

        Assert.Contains($"export interface {selectedInterface}", selected, StringComparison.Ordinal);
        Assert.Contains(selectedProperty, selected, StringComparison.Ordinal);
        Assert.Contains("export interface TaskAppContext", selected, StringComparison.Ordinal);
        Assert.Contains("export interface UiSelector", selected, StringComparison.Ordinal);
        foreach (var other in new[] { "TaskMauiContext", "TaskReactContext", "TaskFlutterContext", "TaskCapacitorContext" }
                     .Where(name => name != selectedInterface))
        {
            Assert.DoesNotContain(other, selected, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SelectTypeDefinitionsForEvidence_FallsBackToFullContractForMixedOrUnknownFrameworks()
    {
        var full = RepositoryModuleContractArtifacts.GetTaskTypeDefinitions();
        var expected = LocalTaskExtractionCoordinator.CompactTypeDefinitions(full);
        SessionVisualTreeSnapshot Tree(string kind, string platform = "ios") => new()
        {
            SnapshotId = $"{kind}-{platform}",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Source = "test",
            VisualTreeKind = kind,
            RuntimePlatform = platform,
            NodeCount = 0,
            Payload = new JsonObject()
        };

        Assert.Equal(expected, LocalTaskExtractionCoordinator.SelectTypeDefinitionsForEvidence(
            full, [Tree("native", "unknown")], null));
        Assert.Equal(expected, LocalTaskExtractionCoordinator.SelectTypeDefinitionsForEvidence(
            full, [Tree("flutter", "ios"), Tree("react-component", "android")], null));
        Assert.DoesNotContain("TaskFlutterContext", LocalTaskExtractionCoordinator.SelectTypeDefinitionsForEvidence(
            full, [Tree("react-component")], null), StringComparison.Ordinal);
        var iosNative = LocalTaskExtractionCoordinator.SelectTypeDefinitionsForEvidence(
            full, [Tree("native")], null);
        Assert.DoesNotContain("AndroidPermissionNames", iosNative, StringComparison.Ordinal);
        Assert.Contains("IosPermissionNames", iosNative, StringComparison.Ordinal);

        var reactCatalog = new JsonObject
        {
            ["tools"] = new JsonArray(new JsonObject
            {
                ["id"] = "react.get_component_tree",
                ["executable"] = true
            })
        };
        Assert.DoesNotContain("TaskFlutterContext", LocalTaskExtractionCoordinator.SelectTypeDefinitionsForEvidence(
            full, [Tree("native")], reactCatalog), StringComparison.Ordinal);
    }

    [Fact]
    public void TrimTypeDefinitionsForPlatform_RemovesOnlyOtherPlatformsPermissionSurface()
    {
        var compact = LocalTaskExtractionCoordinator.CompactTypeDefinitions(
            RepositoryModuleContractArtifacts.GetTaskTypeDefinitions());
        var android = LocalTaskExtractionCoordinator.TrimTypeDefinitionsForPlatform(compact, "android");
        var ios = LocalTaskExtractionCoordinator.TrimTypeDefinitionsForPlatform(compact, "ios");

        Assert.DoesNotContain("IosPermissionNames", android, StringComparison.Ordinal);
        Assert.Contains("AndroidPermissionNames", android, StringComparison.Ordinal);
        Assert.DoesNotContain("AndroidPermissionNames", ios, StringComparison.Ordinal);
        Assert.Contains("IosPermissionNames", ios, StringComparison.Ordinal);
        Assert.Contains("export interface TaskHostUiContext", android, StringComparison.Ordinal);
        Assert.Contains("export interface TaskHostUiContext", ios, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskTypeDefinitionSlicer_KeepsCoreAndRequestedClipboardWithoutUnrelatedFeatures()
    {
        var compact = LocalTaskExtractionCoordinator.CompactTypeDefinitions(
            RepositoryModuleContractArtifacts.GetTaskTypeDefinitions());
        var selected = TaskTypeDefinitionSlicer.Slice(
            compact,
            "Tap Share and copy the area location to the clipboard.",
            "await ansight.ui.tap({ automationId: 'ShareButton' });");

        Assert.Contains("export interface TaskDefinition", selected, StringComparison.Ordinal);
        Assert.Contains("export interface TaskHostUiContext", selected, StringComparison.Ordinal);
        Assert.Contains("export interface TaskClipboardContext", selected, StringComparison.Ordinal);
        Assert.Contains("readonly clipboard: TaskClipboardContext;", selected, StringComparison.Ordinal);
        Assert.DoesNotContain("export interface TaskHostTelemetryContext", selected, StringComparison.Ordinal);
        Assert.DoesNotContain("export interface AndroidPermissionNames", selected, StringComparison.Ordinal);
        Assert.True(selected.Length < compact.Length / 2);
    }

    [Fact]
    public void TaskTypeDefinitionSlicer_PreservesExplicitlyUsedHostFeature()
    {
        var compact = LocalTaskExtractionCoordinator.CompactTypeDefinitions(
            RepositoryModuleContractArtifacts.GetTaskTypeDefinitions());
        var selected = TaskTypeDefinitionSlicer.Slice(
            compact, "Verify a network request.", "await ansight.network.getRequests({});");

        Assert.Contains("readonly network: TaskHostNetworkContext;", selected, StringComparison.Ordinal);
        Assert.Contains("export interface TaskHostNetworkContext", selected, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskTypeDefinitionSlicer_IncludesPermissionConstantsWhenRequested()
    {
        var compact = LocalTaskExtractionCoordinator.CompactTypeDefinitions(
            RepositoryModuleContractArtifacts.GetTaskTypeDefinitions());
        var selected = TaskTypeDefinitionSlicer.Slice(compact, "Grant camera permission.", string.Empty);

        Assert.Contains("readonly permissions: TaskHostPermissionsContext;", selected, StringComparison.Ordinal);
        Assert.Contains("export const IosPermission", selected, StringComparison.Ordinal);
        Assert.Contains("export const AndroidPermission", selected, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTaskContractEvidence_RecoversOriginalDocumentedDeclaration()
    {
        var result = LocalTaskExtractionCoordinator.BuildTaskContractEvidence(new JsonObject
        {
            ["symbol"] = "TaskFlutterContext",
            ["characterOffset"] = 0
        });

        Assert.Contains("@supportedFrameworks flutter", result["sourceChunk"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("getWidgetTree", result["sourceChunk"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(result["nextCharacterOffset"]);
    }

    [Fact]
    public void BuildVisibleTreeEvidence_ReturnsGroundedVisibleNodesWithoutRawPayload()
    {
        var tree = new SessionVisualTreeSnapshot
        {
            SnapshotId = "tree-1",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Source = "test",
            VisualTreeKind = "native",
            RuntimePlatform = "ios",
            NodeCount = 2,
            Payload = new JsonObject
            {
                ["root"] = new JsonObject
                {
                    ["id"] = "root",
                    ["type"] = "UIWindow",
                    ["visible"] = true,
                    ["children"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "button-1",
                        ["automationId"] = "ShareButton",
                        ["text"] = "Share",
                        ["type"] = "UIButton",
                        ["visible"] = true,
                        ["children"] = new JsonArray()
                    })
                }
            }
        };

        var result = LocalTaskExtractionCoordinator.BuildVisibleTreeEvidence(
            [tree],
            LocalTaskSelectorEvidence.Create([tree]),
            new JsonObject { ["snapshotIds"] = new JsonArray("tree-1"), ["nodeOffset"] = 0 });
        var serialized = result.ToJsonString();

        Assert.Contains("ShareButton", serialized, StringComparison.Ordinal);
        Assert.Contains("Share", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"payload\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"children\"", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRawVisualTreeEvidence_PreservesHiddenNodesAndAllPayloadChunks()
    {
        var tree = new SessionVisualTreeSnapshot
        {
            SnapshotId = "tree-raw",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Source = "test",
            VisualTreeKind = "native",
            RuntimePlatform = "ios",
            NodeCount = 2,
            Payload = new JsonObject
            {
                ["root"] = new JsonObject
                {
                    ["id"] = "root",
                    ["children"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "hidden-detail",
                        ["visible"] = false,
                        ["state"] = "requires-expanded-parent"
                    })
                },
                ["additionalCaptureData"] = new string('x', 33_000)
            }
        };

        var first = LocalTaskExtractionCoordinator.BuildRawVisualTreeEvidence(
            [tree], new JsonObject { ["snapshotId"] = "tree-raw", ["characterOffset"] = 0 });
        var nextOffset = first["nextCharacterOffset"]!.GetValue<int>();
        var second = LocalTaskExtractionCoordinator.BuildRawVisualTreeEvidence(
            [tree], new JsonObject { ["snapshotId"] = "tree-raw", ["characterOffset"] = nextOffset });
        var recoveredPayload = first["payloadChunk"]!.GetValue<string>()
                               + second["payloadChunk"]!.GetValue<string>();

        Assert.Equal(tree.Payload.ToJsonString(), recoveredPayload);
        Assert.Contains("hidden-detail", recoveredPayload, StringComparison.Ordinal);
        Assert.Null(second["nextCharacterOffset"]);
    }

    [Fact]
    public void TrimToLatestCompaction_KeepsCheckpointAndFollowingFunctionCall()
    {
        var history = new JsonArray(
            new JsonObject { ["type"] = "message", ["id"] = "initial" },
            new JsonObject { ["type"] = "function_call_output", ["id"] = "old-tool" },
            new JsonObject { ["type"] = "compaction", ["id"] = "checkpoint" },
            new JsonObject { ["type"] = "function_call", ["id"] = "next-tool" });

        LocalTaskExtractionCoordinator.TrimToLatestCompaction(history, 2);

        Assert.Equal(2, history.Count);
        Assert.Equal("checkpoint", history[0]?["id"]?.GetValue<string>());
        Assert.Equal("next-tool", history[1]?["id"]?.GetValue<string>());
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
