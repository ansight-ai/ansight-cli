using System.Text.Json.Nodes;
using Ansight.Host.Models.Session;
using Ansight.Host.Runtime.Tasks;
using Ansight.Host.Workspaces.Catalog;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class HostTimelineTaskExtractorTests
{
    [Fact]
    public void WorkspaceTestExport_ProducesRunnableSchemaWithRecordedJourneyAndNewFinalLabel()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1), 0.5, 0.5),
                CreateTouch("up", startedAt.AddSeconds(1.1), 0.5, 0.5)
            ],
            [
                CreateVisualTree(startedAt.AddSeconds(1), "continue-button", "Continue"),
                CreateVisualTree(startedAt.AddSeconds(2), "confirmation-title", "Order complete")
            ]);

        var result = WorkspaceTestExtractor.Extract(
            snapshot, startedAt, startedAt.AddSeconds(3), "Complete checkout");
        var parsed = WorkspaceTestCatalog.Parse("ansight/tests", "ansight/tests/complete-checkout.yaml", result.Source);

        Assert.Equal("complete-checkout", parsed.TestId);
        Assert.Equal(snapshot.AppId, parsed.AppId);
        Assert.Contains("1. ", parsed.Prompt, StringComparison.Ordinal);
        Assert.Contains("Order complete", parsed.Validation.Assertions[0], StringComparison.Ordinal);
        Assert.Equal(1, result.GeneratedActionCount);
    }

    [Fact]
    public void WorkspaceTestExport_UsesExplicitOutcomeAndOmitsCapturedInputValue()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [],
            [
                CreateInputVisualTree(startedAt.AddSeconds(1), "tree-1", string.Empty),
                CreateInputVisualTree(startedAt.AddSeconds(2), "tree-2", "private@example.test")
            ]);

        var result = WorkspaceTestExtractor.Extract(
            snapshot, startedAt, startedAt.AddSeconds(3), "Search accounts",
            ["The results screen shows the matching account"],
            "Inspect the final visual tree.");
        var parsed = WorkspaceTestCatalog.Parse("ansight/tests", "ansight/tests/search-accounts.yaml", result.Source);

        Assert.Equal("The results screen shows the matching account", parsed.Validation.Assertions[0]);
        Assert.Equal("Inspect the final visual tree.", parsed.Validation.Prompt);
        Assert.False(parsed.Enabled);
        Assert.DoesNotContain("private@example.test", result.Source, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("captured text was omitted", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkspaceTestExport_DisablesDraftWhenNoObservableOutcomeIsAvailable()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1), 0.5, 0.5),
                CreateTouch("up", startedAt.AddSeconds(1.1), 0.5, 0.5)
            ],
            [CreateVisualTree(startedAt.AddSeconds(1), "continue-button", "Continue")]);

        var result = WorkspaceTestExtractor.Extract(
            snapshot, startedAt, startedAt.AddSeconds(2), "Continue checkout");
        var parsed = WorkspaceTestCatalog.Parse("ansight/tests", "ansight/tests/continue-checkout.yaml", result.Source);

        Assert.False(parsed.Enabled);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("Replace the generic outcome assertion", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkspaceTestExport_UsesOnlySelectedAnnotatedTaskRanges()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(startedAt, [], [],
        [
            new SessionAnnotation { AnnotationId = "search", StartUtc = startedAt.AddSeconds(1), EndUtc = startedAt.AddSeconds(3), Label = "Find the account", Notes = "Use the customer name" },
            new SessionAnnotation { AnnotationId = "verify", StartUtc = startedAt.AddSeconds(4), EndUtc = startedAt.AddSeconds(6), Label = "Verify the balance" },
            new SessionAnnotation { AnnotationId = "outside", StartUtc = startedAt.AddSeconds(11), EndUtc = startedAt.AddSeconds(12), Label = "Unrelated action" }
        ]);

        var result = WorkspaceTestExtractor.Extract(
            snapshot, startedAt, startedAt.AddSeconds(10), "Review account",
            ["The balance is visible"], taskSectionIds: ["verify", "search"]);
        var parsed = WorkspaceTestCatalog.Parse("ansight/tests", "ansight/tests/review-account.yaml", result.Source);

        Assert.Contains("Find the account — Use the customer name", parsed.Prompt, StringComparison.Ordinal);
        Assert.Contains("Verify the balance", parsed.Prompt, StringComparison.Ordinal);
        Assert.True(parsed.Prompt.IndexOf("Find the account", StringComparison.Ordinal) < parsed.Prompt.IndexOf("Verify the balance", StringComparison.Ordinal));
        Assert.DoesNotContain("Unrelated action", parsed.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("recorded sequence", parsed.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.True(parsed.Enabled);
        Assert.Throws<ArgumentException>(() => WorkspaceTestExtractor.Extract(
            snapshot, startedAt, startedAt.AddSeconds(10), "Review account",
            ["The balance is visible"], taskSectionIds: ["outside"]));
    }

    [Fact]
    public void WorkspaceTestExport_UsesAnnotatedIntentInsteadOfSwipeCoordinates()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1), 0.8, 0.7),
                CreateTouch("move", startedAt.AddSeconds(1.1), 0.7, 0.75),
                CreateTouch("up", startedAt.AddSeconds(1.2), 0.6, 0.8)
            ],
            [],
            [new SessionAnnotation
            {
                AnnotationId = "map",
                StartUtc = startedAt.AddMilliseconds(500),
                EndUtc = startedAt.AddSeconds(2),
                Label = "Find Eagle Rock on the map and open its details"
            }]);

        var result = WorkspaceTestExtractor.Extract(
            snapshot, startedAt, startedAt.AddSeconds(3), "Open Eagle Rock",
            ["Eagle Rock details are visible"], taskSectionIds: ["map"]);
        var parsed = WorkspaceTestCatalog.Parse("ansight/tests", "ansight/tests/open-eagle-rock.yaml", result.Source);

        Assert.Contains("Find Eagle Rock on the map and open its details", parsed.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("ansight_swipe_ui", parsed.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("0.8", parsed.Prompt, StringComparison.Ordinal);
        Assert.True(parsed.Enabled);
    }

    [Fact]
    public void WorkspaceTestExport_InfersNewFinalLabelFromTheLastAction()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var finalTree = CreateVisualTree(startedAt.AddSeconds(5), "copied-toast", "Copied to clipboard");
        var finalRoot = finalTree.Payload!["root"]!.AsObject();
        finalRoot["children"]!.AsArray().Add(new JsonObject
        {
            ["label"] = "Lengths range from ~6m to ~30m. Most climbs are between ~10m and ~15m."
        });
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(4), 0.5, 0.5),
                CreateTouch("up", startedAt.AddSeconds(4.1), 0.5, 0.5)
            ],
            [
                CreateVisualTree(startedAt.AddSeconds(1), "home", "Home"),
                CreateVisualTree(startedAt.AddSeconds(3), "copy-button", "Lengths range from ~6m to ~30m. Most climbs are between ~10m and ~15m."),
                finalTree
            ]);

        var result = WorkspaceTestExtractor.Extract(
            snapshot, startedAt, startedAt.AddSeconds(6), "Copy location");
        var parsed = WorkspaceTestCatalog.Parse("ansight/tests", "ansight/tests/copy-location.yaml", result.Source);

        Assert.Contains("Copied to clipboard", parsed.Validation.Assertions[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Lengths range", parsed.Validation.Assertions[0], StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceTestRefiner_AcceptsShortIdentityAndRejectsRawReplay()
    {
        const string valid = """
            schemaVersion: 1
            id: "copy-location"
            name: "Copy location"
            appId: "com.example.app"
            prompt: "Open the area and copy its location."
            validation:
              prompt: "Inspect the final UI."
              assertions:
                - "A copied confirmation is visible."
            """;

        var definition = WorkspaceTestRefiner.ValidateRefinedSource(valid, "com.example.app");
        Assert.Equal("copy-location", definition.TestId);
        Assert.Throws<InvalidDataException>(() => WorkspaceTestRefiner.ValidateRefinedSource(
            valid.Replace("com.example.app", "com.other.app", StringComparison.Ordinal),
            "com.example.app"));
        Assert.Throws<InvalidDataException>(() => WorkspaceTestRefiner.ValidateRefinedSource(
            valid.Replace("Open the area", "Call ansight_swipe_ui and open the area", StringComparison.Ordinal),
            "com.example.app"));
        Assert.Throws<InvalidDataException>(() => WorkspaceTestRefiner.ValidateRefinedSource(
            valid.Replace("Copy location", "Recorded com.example.app workflow", StringComparison.Ordinal),
            "com.example.app"));
        Assert.Throws<InvalidDataException>(() => WorkspaceTestRefiner.ValidateRefinedSource(
            valid.Replace("Open the area and copy its location.",
                "In the com.example.app app, complete this journey using the visible UI. Open the area.", StringComparison.Ordinal),
            "com.example.app"));
    }

    [Fact]
    public void AppiumExport_GeneratesRunnableDraftWithRecordedIdAndCoordinateFallback()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1), 0.5, 0.5),
                CreateTouch("up", startedAt.AddSeconds(1.1), 0.5, 0.5),
                CreateTouch("down", startedAt.AddSeconds(2), 0.25, 0.75),
                CreateTouch("up", startedAt.AddSeconds(2.1), 0.25, 0.75)
            ],
            [CreateVisualTree(startedAt.AddSeconds(1), "continue-button", "Continue")]);

        var result = AppiumScriptExtractor.Extract(snapshot, startedAt, startedAt.AddSeconds(3), "Continue checkout");

        Assert.Equal("continue-checkout", result.SuggestedName);
        Assert.Equal(2, result.GeneratedActionCount);
        Assert.Contains("import { remote } from 'webdriverio'", result.Source, StringComparison.Ordinal);
        Assert.Contains("recordedTarget(driver, \"continue-button\", null)", result.Source, StringComparison.Ordinal);
        Assert.Contains("touchAt(driver, 0.25, 0.75)", result.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("// Captured", result.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("// REVIEW:", result.Source, StringComparison.Ordinal);
        Assert.Contains("// Review before running:\n// - Confirm the starting state and add an outcome assertion.", result.Source, StringComparison.Ordinal);
        Assert.EndsWith("// - Verify coordinate taps on the target device.\n", result.Source, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, item => item.Contains("viewport coordinate", StringComparison.Ordinal));
    }

    [Fact]
    public void AppiumExport_ConvertsInputAndSwipe()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(3), 0.5, 0.8),
                CreateTouch("move", startedAt.AddSeconds(3.1), 0.5, 0.4),
                CreateTouch("up", startedAt.AddSeconds(3.25), 0.5, 0.2)
            ],
            [
                CreateInputVisualTree(startedAt.AddSeconds(1), "tree-1", string.Empty),
                CreateInputVisualTree(startedAt.AddSeconds(2), "tree-2", "ada@example.test")
            ]);

        var result = AppiumScriptExtractor.Extract(snapshot, startedAt, startedAt.AddSeconds(4), "Enter and scroll");

        Assert.Equal(2, result.GeneratedActionCount);
        Assert.Contains(".setValue(\"ada@example.test\")", result.Source, StringComparison.Ordinal);
        Assert.Contains("touchAt(driver, 0.5, 0.8, 0.5, 0.2, 250)", result.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void MaestroExport_GeneratesIdTapAndReviewableOutcome()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1), 0.5, 0.5),
                CreateTouch("up", startedAt.AddSeconds(1).AddMilliseconds(100), 0.5, 0.5)
            ],
            [CreateVisualTree(startedAt.AddSeconds(1), "continue-button", "Continue")]);

        var result = MaestroFlowExtractor.Extract(
            snapshot,
            startedAt,
            startedAt.AddSeconds(2),
            "Continue checkout");

        Assert.Equal("continue-checkout", result.SuggestedName);
        Assert.Equal(1, result.GeneratedActionCount);
        Assert.Contains("appId: \"com.example.app\"", result.Source, StringComparison.Ordinal);
        Assert.Contains("- tapOn:\n    id: \"^continue-button$\"", result.Source, StringComparison.Ordinal);
        Assert.Contains("- launchApp", result.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("# Captured", result.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("# REVIEW:", result.Source, StringComparison.Ordinal);
        Assert.EndsWith("# Review before running:\n# - Confirm the starting state and add an outcome assertion.\n", result.Source, StringComparison.Ordinal);
        MaestroFlowRefiner.ValidateSource(result.Source, snapshot.AppId);
    }

    [Fact]
    public void MaestroExport_ConvertsInputAndSwipeWithoutMaskedValues()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(3), 0.5, 0.8),
                CreateTouch("move", startedAt.AddSeconds(3.1), 0.5, 0.4),
                CreateTouch("up", startedAt.AddSeconds(3.25), 0.5, 0.2)
            ],
            [
                CreateInputVisualTree(startedAt.AddSeconds(1), "tree-1", string.Empty),
                CreateInputVisualTree(startedAt.AddSeconds(2), "tree-2", "ada@example.test")
            ]);

        var result = MaestroFlowExtractor.Extract(
            snapshot,
            startedAt,
            startedAt.AddSeconds(4),
            "Enter and scroll");

        Assert.Equal(2, result.GeneratedActionCount);
        Assert.Contains("id: \"^email-field$\"", result.Source, StringComparison.Ordinal);
        Assert.Contains("- eraseText: 100", result.Source, StringComparison.Ordinal);
        Assert.Contains("- inputText: \"ada@example.test\"", result.Source, StringComparison.Ordinal);
        Assert.Contains("start: 50%, 80%", result.Source, StringComparison.Ordinal);
        Assert.Contains("end: 50%, 20%", result.Source, StringComparison.Ordinal);
        Assert.Contains("duration: 250", result.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void MaestroExport_RoundsFractionalSwipeCoordinatesToSupportedPercentages()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1), 0.77861, 0.58963),
                CreateTouch("move", startedAt.AddSeconds(1.1), 0.82, 0.4),
                CreateTouch("up", startedAt.AddSeconds(1.25), 0.87148, 0.2132)
            ],
            []);

        var result = MaestroFlowExtractor.Extract(snapshot, startedAt, startedAt.AddSeconds(2), "Scroll ascents");

        Assert.Contains("start: 78%, 59%", result.Source, StringComparison.Ordinal);
        Assert.Contains("end: 87%, 21%", result.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("77.861%", result.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void MaestroExport_FallsBackToReviewedCoordinateTapWithoutATree()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1), 0.25, 0.75),
                CreateTouch("up", startedAt.AddSeconds(1.1), 0.25, 0.75)
            ],
            []);

        var result = MaestroFlowExtractor.Extract(
            snapshot,
            startedAt,
            startedAt.AddSeconds(2),
            "Tap map point");

        Assert.Equal(1, result.GeneratedActionCount);
        Assert.Contains("point: \"25%,75%\"", result.Source, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("confirm it on the target device", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MaestroExport_UsesSmallestIdTargetFromPixelBoundsWithoutScreenshotLink()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var tree = new SessionVisualTreeSnapshot
        {
            SnapshotId = "pixel-tree",
            CapturedAtUtc = startedAt.AddSeconds(1),
            Source = "test",
            NodeCount = 3,
            Payload = new JsonObject
            {
                ["coordinateSpace"] = new JsonObject
                {
                    ["x"] = 10,
                    ["y"] = 20,
                    ["width"] = 400,
                    ["height"] = 800
                },
                ["root"] = new JsonObject
                {
                    ["automationId"] = "whole-page",
                    ["bounds"] = new JsonArray(10, 20, 400, 800, 10, 20, 400, 800),
                    ["children"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["automationId"] = "full-screen-overlay",
                            ["bounds"] = new JsonArray(10, 20, 400, 800, 10, 20, 400, 800)
                        },
                        new JsonObject
                        {
                            ["automationId"] = "profile-tab",
                            ["bounds"] = new JsonArray(310, 740, 100, 80, 310, 740, 100, 80)
                        }
                    }
                }
            }
        };
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1), 0.8, 0.95),
                CreateTouch("up", startedAt.AddSeconds(1.1), 0.8, 0.95)
            ],
            [tree]);

        var result = MaestroFlowExtractor.Extract(snapshot, startedAt, startedAt.AddSeconds(2), "Open profile");

        Assert.Contains("id: \"^profile-tab$\"", result.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("point:", result.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppiumExport_UsesPixelBoundsFromNativeRootWithoutScreenshotLink()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var tree = new SessionVisualTreeSnapshot
        {
            SnapshotId = "native-tree",
            CapturedAtUtc = startedAt.AddSeconds(1),
            Source = "test",
            NodeCount = 2,
            Payload = new JsonObject
            {
                ["root"] = new JsonObject
                {
                    ["bounds"] = new JsonObject
                    {
                        ["x"] = 0,
                        ["y"] = 0,
                        ["width"] = 402,
                        ["height"] = 874
                    },
                    ["children"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["automationId"] = "profile-tab",
                            ["bounds"] = new JsonObject
                            {
                                ["x"] = 301,
                                ["y"] = 790,
                                ["width"] = 101,
                                ["height"] = 84
                            }
                        }
                    }
                }
            }
        };
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1), 0.8, 0.95),
                CreateTouch("up", startedAt.AddSeconds(1.1), 0.8, 0.95)
            ],
            [tree]);

        var result = AppiumScriptExtractor.Extract(snapshot, startedAt, startedAt.AddSeconds(2), "Open profile");

        Assert.Contains("recordedTarget(driver, \"profile-tab\", null)", result.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("await touchAt(driver", result.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void MaestroValidation_RejectsAnUnrelatedAppOrUnsupportedCommand()
    {
        Assert.Throws<InvalidDataException>(() => MaestroFlowRefiner.ValidateSource(
            "appId: other.app\n---\n- tapOn: Continue\n",
            "com.example.app"));
        Assert.Throws<InvalidDataException>(() => MaestroFlowRefiner.ValidateSource(
            "appId: com.example.app\n---\n- runScript: dangerous.js\n",
            "com.example.app"));
        MaestroFlowRefiner.ValidateSource(
            "appId: \"com.example.app\"\n---\n- launchApp\n- tapOn:\n    id: \"^continue-button$\"\n",
            "com.example.app");
    }

    [Fact]
    public void Extract_GeneratesWaitTapAndStableAssertionForAutomationIdTarget()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1), 0.5, 0.5),
                CreateTouch("up", startedAt.AddSeconds(1).AddMilliseconds(100), 0.5, 0.5)
            ],
            [CreateVisualTree(startedAt.AddSeconds(1), "continue-button", "Continue")]);

        var result = TimelineTaskExtractor.Extract(
            snapshot,
            startedAt,
            startedAt.AddSeconds(2),
            "Continue checkout");

        Assert.Equal(1, result.GestureCount);
        Assert.Equal(1, result.GeneratedActionCount);
        Assert.Empty(result.Diagnostics);
        Assert.Contains("ansight.ui.waitFor", result.Source, StringComparison.Ordinal);
        Assert.Contains("ansight.ui.tap", result.Source, StringComparison.Ordinal);
        Assert.Contains("automationId=continue-button", result.Source, StringComparison.Ordinal);
        Assert.Contains("workflow-stable", result.Source, StringComparison.Ordinal);
        Assert.Equal(
            "Tap the visible control with automation ID \"continue-button\".",
            Assert.Single(result.ReplayInstructions));
        AssertTaskSourceLoads(result.Source, "continue-checkout");
    }

    [Fact]
    public void Extract_GeneratesDirectionalSwipeAndReportsUnsupportedGesture()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1), 0.5, 0.8, pointerId: 1),
                CreateTouch("move", startedAt.AddSeconds(1).AddMilliseconds(100), 0.5, 0.4, pointerId: 1),
                CreateTouch("up", startedAt.AddSeconds(1).AddMilliseconds(250), 0.5, 0.2, pointerId: 1),
                CreateTouch("down", startedAt.AddSeconds(3), 0.3, 0.3, pointerId: 2),
                CreateTouch("up", startedAt.AddSeconds(3).AddMilliseconds(700), 0.3, 0.3, pointerId: 2)
            ],
            []);

        var result = TimelineTaskExtractor.Extract(
            snapshot,
            startedAt,
            startedAt.AddSeconds(4),
            "Scroll results");

        Assert.Equal(2, result.GestureCount);
        Assert.Equal(1, result.GeneratedActionCount);
        Assert.Single(result.Diagnostics);
        Assert.Contains("long press", result.Diagnostics[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ansight.ui.swipe", result.Source, StringComparison.Ordinal);
        Assert.Contains("orientation: \"N\"", result.Source, StringComparison.Ordinal);
        var replayStep = Assert.Single(result.ReplaySteps);
        Assert.Equal(
            "Replay the recorded swipe exactly once by calling ansight_swipe_ui with startNormalizedX=0.5, "
            + "startNormalizedY=0.8, endNormalizedX=0.5, endNormalizedY=0.2, and durationMs=250. "
            + "Do not recenter, approximate, or repeat the gesture.",
            replayStep.Instruction);
        Assert.Equal(0.5, replayStep.SourceEvidence?.StartNormalizedX);
        Assert.Equal(0.8, replayStep.SourceEvidence?.StartNormalizedY);
        Assert.Equal(0.5, replayStep.SourceEvidence?.EndNormalizedX);
        Assert.Equal(0.2, replayStep.SourceEvidence?.EndNormalizedY);
        Assert.Equal(250, replayStep.SourceEvidence?.DurationMilliseconds);
        Assert.Contains("REVIEW:", result.Source, StringComparison.Ordinal);
        AssertTaskSourceLoads(result.Source, "scroll-results");
    }

    [Fact]
    public void Extract_DoesNotTurnACancelledScrollIntoACompletedSwipe()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1), 0.5, 0.8),
                CreateTouch("move", startedAt.AddSeconds(1.1), 0.5, 0.4),
                CreateTouch("cancel", startedAt.AddSeconds(1.2), 0.5, 0.2)
            ],
            []);

        var result = TimelineTaskExtractor.Extract(
            snapshot,
            startedAt,
            startedAt.AddSeconds(2),
            "Interrupted scroll",
            includeCoordinateFallbackTaps: true);

        Assert.Equal(1, result.GestureCount);
        Assert.Equal(0, result.GeneratedActionCount);
        Assert.Empty(result.ReplaySteps);
        Assert.Contains("was cancelled", Assert.Single(result.Diagnostics), StringComparison.Ordinal);
        Assert.DoesNotContain("ansight.ui.swipe", result.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("cancelled")]
    [InlineData("canceled")]
    public void ReplayPlanner_SkipsCancelledTapAndKeepsCompletedActions(string cancelAction)
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1), 0.5, 0.5),
                CreateTouch(cancelAction, startedAt.AddSeconds(1.1), 0.5, 0.5),
                CreateTouch("down", startedAt.AddSeconds(2), 0.25, 0.75),
                CreateTouch("up", startedAt.AddSeconds(2.1), 0.25, 0.75)
            ],
            [CreateVisualTree(startedAt.AddSeconds(1), "continue-button", "Continue")]);

        var plan = AnsightReplayPlanner.Build(snapshot, startedAt, startedAt.AddSeconds(3));

        var step = Assert.Single(plan.Steps);
        Assert.Equal(1, step.Sequence);
        Assert.Equal(startedAt.AddSeconds(2), step.CapturedAtUtc);
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Contains("was cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_CreatesExplicitlyFailingDraftWhenNoReproducibleActionsExist()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1), 0.5, 0.5),
                CreateTouch("up", startedAt.AddSeconds(1).AddMilliseconds(100), 0.5, 0.5)
            ],
            []);

        var result = TimelineTaskExtractor.Extract(
            snapshot,
            startedAt,
            startedAt.AddSeconds(2),
            "Unresolved tap");

        Assert.Equal(0, result.GeneratedActionCount);
        Assert.Empty(result.ReplayInstructions);
        Assert.Contains("id: \"review-required\"", result.Source, StringComparison.Ordinal);
        Assert.Contains(".toBe(true)", result.Source, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("no stable UI selector", StringComparison.OrdinalIgnoreCase));
        AssertTaskSourceLoads(result.Source, "unresolved-tap");
    }

    [Fact]
    public void ReplayPlanner_UsesRecordedCoordinatesWhenATapHasNoStableSelector()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1), 0.25, 0.75),
                CreateTouch("up", startedAt.AddSeconds(1).AddMilliseconds(120), 0.25, 0.75)
            ],
            []);

        var plan = AnsightReplayPlanner.Build(snapshot, startedAt, startedAt.AddSeconds(2));

        var step = Assert.Single(plan.Steps);
        Assert.Equal("tap", step.Kind);
        Assert.Contains("normalizedX=0.25", step.Instruction, StringComparison.Ordinal);
        Assert.Contains("normalizedY=0.75", step.Instruction, StringComparison.Ordinal);
        Assert.Equal(120, step.SourceEvidence?.DurationMilliseconds);
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Contains("recorded normalized viewport point", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_DiffsAndCompactsVisualTreeInputValuesIntoTextEntry()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1), 0.5, 0.25),
                CreateTouch("up", startedAt.AddSeconds(1).AddMilliseconds(100), 0.5, 0.25)
            ],
            [
                CreateInputVisualTree(startedAt.AddSeconds(1), "tree-1", string.Empty),
                CreateInputVisualTree(startedAt.AddSeconds(1.5), "tree-2", "ada"),
                CreateInputVisualTree(startedAt.AddSeconds(2), "tree-3", "ada@example.test")
            ]);

        var result = TimelineTaskExtractor.Extract(
            snapshot,
            startedAt,
            startedAt.AddSeconds(3),
            "Enter email");

        Assert.Equal(1, result.GeneratedActionCount);
        Assert.Contains("ansight.ui.typeText", result.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("ansight.ui.tap", result.Source, StringComparison.Ordinal);
        Assert.Contains("ada@example.test", result.Source, StringComparison.Ordinal);
        var replayStep = Assert.Single(result.ReplaySteps);
        Assert.Equal("input", replayStep.Kind);
        Assert.Equal(startedAt.AddSeconds(2), replayStep.CapturedAtUtc);
        Assert.Equal("tree-3", replayStep.SourceEvidence?.VisualTreeSnapshotId);
        Assert.Equal(
            "Enter \"ada@example.test\" into the visible input field with automation ID \"email-field\", replacing any existing text.",
            replayStep.Instruction);
        AssertTaskSourceLoads(result.Source, "enter-email");
    }

    [Fact]
    public void Extract_UsesAnEmptyVisualTreeValueToClearAnInput()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [],
            [
                CreateInputVisualTree(startedAt.AddSeconds(1), "tree-1", "old value"),
                CreateInputVisualTree(startedAt.AddSeconds(2), "tree-2", string.Empty)
            ]);

        var result = TimelineTaskExtractor.Extract(
            snapshot,
            startedAt,
            startedAt.AddSeconds(3),
            "Clear email");

        var replayStep = Assert.Single(result.ReplaySteps);
        Assert.Equal("input", replayStep.Kind);
        Assert.Equal(
            "Clear the visible input field with automation ID \"email-field\".",
            replayStep.Instruction);
        Assert.Contains("\"value\":\"\"", result.Source, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("autofill", StringComparison.OrdinalIgnoreCase));
        AssertTaskSourceLoads(result.Source, "clear-email");
    }

    [Fact]
    public void Extract_OrdersAVisualTreeInputBeforeTheTouchThatExposedItsFinalValue()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [
                CreateTouch("down", startedAt.AddSeconds(1.1), 0.5, 0.25),
                CreateTouch("up", startedAt.AddSeconds(1.2), 0.5, 0.25),
                CreateTouch("down", startedAt.AddSeconds(2), 0.5, 0.75),
                CreateTouch("up", startedAt.AddSeconds(2.1), 0.5, 0.75)
            ],
            [
                CreateInputAndSubmitVisualTree(startedAt.AddSeconds(1), "tree-1", string.Empty),
                CreateInputAndSubmitVisualTree(startedAt.AddSeconds(2.2), "tree-2", "ada@example.test")
            ]);

        var result = TimelineTaskExtractor.Extract(
            snapshot,
            startedAt,
            startedAt.AddSeconds(3),
            "Submit email");

        Assert.Collection(
            result.ReplaySteps,
            input =>
            {
                Assert.Equal("input", input.Kind);
                Assert.Equal(startedAt.AddSeconds(2.2), input.CapturedAtUtc);
                Assert.Equal("tree-2", input.SourceEvidence?.VisualTreeSnapshotId);
            },
            submit =>
            {
                Assert.Equal("tap", submit.Kind);
                Assert.Contains("submit-button", submit.Instruction, StringComparison.Ordinal);
            });
        Assert.DoesNotContain(
            result.ReplaySteps,
            step => step.Kind == "tap"
                    && step.Instruction.Contains("email-field", StringComparison.Ordinal));
        AssertTaskSourceLoads(result.Source, "submit-email");
    }

    [Fact]
    public void Extract_DoesNotReplayMaskedVisualTreeValuesAsLiteralText()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [],
            [
                CreateInputVisualTree(startedAt.AddSeconds(1), "tree-1", string.Empty, "password-field", "Password"),
                CreateInputVisualTree(startedAt.AddSeconds(2), "tree-2", "••••••", "password-field", "Password")
            ]);

        var result = TimelineTaskExtractor.Extract(
            snapshot,
            startedAt,
            startedAt.AddSeconds(3),
            "Enter password");

        Assert.Empty(result.ReplaySteps);
        Assert.DoesNotContain("••••••", result.Source, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("host-managed secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extract_ReportsWhenAnInputHasNoComparableVisualTreeState()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var snapshot = CreateSnapshot(
            startedAt,
            [],
            [CreateInputVisualTree(startedAt.AddSeconds(1), "tree-1", "prefilled or typed")]);

        var result = TimelineTaskExtractor.Extract(
            snapshot,
            startedAt,
            startedAt.AddSeconds(2),
            "Ambiguous input");

        Assert.Empty(result.ReplaySteps);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("no comparable consecutive", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertTaskSourceLoads(string source, string fileName)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"ansight-task-extraction-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(rootPath, "ansight", "tasks");
        try
        {
            Directory.CreateDirectory(taskDirectory);
            File.WriteAllText(Path.Combine(taskDirectory, $"{fileName}.ts"), source);

            var loadResult = RepositoryTaskLoader.Load(
                rootPath,
                "com.example.app",
                toolName => new RepositoryTaskHostToolDescriptor(
                    toolName,
                    RepositoryJavaScriptApiMethods.ResolveHostApiMethod(toolName).FeatureName,
                    RepositoryJavaScriptApiMethods.ResolveHostApiMethod(toolName).MethodName,
                    RepositoryTaskHostToolApplicability.SessionBound));

            Assert.Empty(loadResult.Warnings);
            Assert.Single(loadResult.Tasks);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    private static AppSessionSnapshot CreateSnapshot(
        DateTimeOffset startedAt,
        IReadOnlyList<SessionTouchInputRecord> touches,
        IReadOnlyList<SessionVisualTreeSnapshot> visualTrees,
        IReadOnlyList<SessionAnnotation>? annotations = null)
    {
        return new AppSessionSnapshot
        {
            SessionId = "recorded-session",
            AppId = "com.example.app",
            ClientName = "Example",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = startedAt,
            ConfigId = null,
            Status = "Completed",
            LastUpdatedUtc = startedAt.AddMinutes(1),
            IsHistorical = true,
            Touches = touches,
            VisualTreeSnapshots = visualTrees,
            Annotations = annotations ?? [],
            MetricChannels = [],
            Metrics = []
        };
    }

    private static SessionTouchInputRecord CreateTouch(
        string action,
        DateTimeOffset capturedAt,
        double x,
        double y,
        long pointerId = 1)
    {
        return new SessionTouchInputRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Action = action,
            CapturedAtUtc = capturedAt,
            PointerId = pointerId,
            PointerIndex = 0,
            PointerCount = 1,
            X = x,
            Y = y,
            NormalizedX = x,
            NormalizedY = y,
            CoordinateUnit = "normalized"
        };
    }

    private static SessionVisualTreeSnapshot CreateVisualTree(
        DateTimeOffset capturedAt,
        string automationId,
        string label)
    {
        return new SessionVisualTreeSnapshot
        {
            SnapshotId = "tree-1",
            CapturedAtUtc = capturedAt,
            Source = "test",
            NodeCount = 1,
            Payload = new JsonObject
            {
                ["root"] = new JsonObject
                {
                    ["id"] = "node-1",
                    ["type"] = "Button",
                    ["automationId"] = automationId,
                    ["label"] = label,
                    ["normalizedBounds"] = new JsonObject
                    {
                        ["x"] = 0.4,
                        ["y"] = 0.4,
                        ["width"] = 0.2,
                        ["height"] = 0.2
                    },
                    ["children"] = new JsonArray()
                }
            }
        };
    }

    private static SessionVisualTreeSnapshot CreateInputVisualTree(
        DateTimeOffset capturedAt,
        string snapshotId,
        string value,
        string automationId = "email-field",
        string label = "Email")
    {
        return new SessionVisualTreeSnapshot
        {
            SnapshotId = snapshotId,
            CapturedAtUtc = capturedAt,
            Source = "test",
            NodeCount = 1,
            Payload = new JsonObject
            {
                ["root"] = new JsonObject
                {
                    ["id"] = $"{snapshotId}-input",
                    ["type"] = "Entry",
                    ["automationId"] = automationId,
                    ["label"] = label,
                    ["role"] = "textbox",
                    ["supportedActions"] = new JsonArray("tap", "focus", "typeText"),
                    ["visual"] = new JsonObject
                    {
                        ["value"] = value
                    },
                    ["normalizedBounds"] = new JsonObject
                    {
                        ["x"] = 0.1,
                        ["y"] = 0.2,
                        ["width"] = 0.8,
                        ["height"] = 0.1
                    },
                    ["children"] = new JsonArray()
                }
            }
        };
    }

    private static SessionVisualTreeSnapshot CreateInputAndSubmitVisualTree(
        DateTimeOffset capturedAt,
        string snapshotId,
        string value)
    {
        var tree = CreateInputVisualTree(capturedAt, snapshotId, value);
        var input = tree.Payload["root"]!.DeepClone();
        return new SessionVisualTreeSnapshot
        {
            SnapshotId = snapshotId,
            CapturedAtUtc = capturedAt,
            Source = "test",
            NodeCount = 3,
            Payload = new JsonObject
            {
                ["root"] = new JsonObject
                {
                    ["id"] = $"{snapshotId}-root",
                    ["type"] = "Page",
                    ["normalizedBounds"] = new JsonObject
                    {
                        ["x"] = 0,
                        ["y"] = 0,
                        ["width"] = 1,
                        ["height"] = 1
                    },
                    ["children"] = new JsonArray
                    {
                        input,
                        new JsonObject
                        {
                            ["id"] = $"{snapshotId}-submit",
                            ["type"] = "Button",
                            ["automationId"] = "submit-button",
                            ["label"] = "Submit",
                            ["normalizedBounds"] = new JsonObject
                            {
                                ["x"] = 0.1,
                                ["y"] = 0.7,
                                ["width"] = 0.8,
                                ["height"] = 0.1
                            },
                            ["children"] = new JsonArray()
                        }
                    }
                }
            }
        };
    }
}
