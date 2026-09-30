using System.Text.Json.Nodes;
using Ansight.Host.Models.Session;
using Ansight.Host.Replay;
using Ansight.Host.Runtime.Sanitization;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Replay;

public sealed class LocalTaskSelectorEvidenceTests
{
    [Fact]
    public void Validate_AcceptsSelectorsObservedInVisibleVisualTreeNodes()
    {
        var evidence = LocalTaskSelectorEvidence.Create(
        [
            CreateTree(
                "after-open",
                new JsonObject
                {
                    ["id"] = "close",
                    ["type"] = "MauiButton",
                    ["automationId"] = "area-filters-close-button",
                    ["label"] = "Close",
                    ["role"] = "button",
                    ["visible"] = true,
                    ["supportedActions"] = new JsonArray("tap"),
                    ["children"] = new JsonArray()
                },
                new JsonObject
                {
                    ["id"] = "title",
                    ["type"] = "MauiLabel",
                    ["text"] = "Area filters",
                    ["role"] = "text",
                    ["visible"] = true,
                    ["children"] = new JsonArray()
                })
        ]);
        const string source = """
                              await ansight.ui.find({ automationId: "area-filters-close-button", visible: true });
                              await ansight.ui.waitFor({ text: "Area filters", role: "text", visible: true });
                              """;

        Assert.Empty(LocalTaskSelectorEvidence.Validate(source, evidence));
    }

    [Fact]
    public void Validate_RejectsFrameworkTypeThatWasNotExposedByUiEvidence()
    {
        var evidence = LocalTaskSelectorEvidence.Create(
        [
            CreateTree(
                "after-open",
                new JsonObject
                {
                    ["id"] = "title",
                    ["type"] = "MauiLabel",
                    ["text"] = "Area filters",
                    ["role"] = "text",
                    ["visible"] = true,
                    ["children"] = new JsonArray()
                })
        ]);
        const string source = """
                              await ansight.ui.waitFor({ type: "AreaFiltersSheet", visible: true });
                              """;

        var issue = Assert.Single(LocalTaskSelectorEvidence.Validate(source, evidence));

        Assert.Contains("type=\"AreaFiltersSheet\"", issue.Message, StringComparison.Ordinal);
        Assert.Contains("visual-tree or OCR", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptsTextObservedOnlyByScreenshotOcr()
    {
        var capturedAtUtc = DateTimeOffset.UtcNow;
        var frame = new SessionImageFrame
        {
            FrameId = "frame-after-open",
            CapturedAtUtc = capturedAtUtc,
            Format = "jpeg",
            Width = 390,
            Height = 844,
            Quality = 90,
            ByteCount = 100
        };
        var evidence = LocalTaskSelectorEvidence.Create([], screenshotFrameCount: 1)
            .WithOcrBlocks(
                frame,
                [
                    new SessionScreenshotTextBlock(
                        "Area filters",
                        96,
                        new SessionSanitizationRegion(20, 40, 120, 24))
                ]);
        const string source = """
                              await ansight.ui.waitFor({ text: "Area filters", role: "text", visible: true });
                              """;

        Assert.Empty(LocalTaskSelectorEvidence.Validate(source, evidence));
    }

    [Fact]
    public void ExtractSelectorCalls_RequiresLiteralInlineSelectors()
    {
        const string source = """
                              const selector = { automationId: "area-filters-close-button" };
                              await ansight.ui.tap(selector);
                              await ansight.ui.waitFor({ type: expectedType });
                              await ansight.ui.find({ automationId });
                              await ansight.ui.assert({ ...selector, visible: true });
                              """;

        var calls = LocalTaskSelectorEvidence.ExtractSelectorCalls(source);

        Assert.False(calls[0].IsStaticallyInspectable);
        Assert.Equal(["type"], calls[1].DynamicFields);
        Assert.Equal(["automationId"], calls[2].DynamicFields);
        Assert.False(calls[3].IsStaticallyInspectable);
    }

    [Fact]
    public void SuggestSelectors_PrioritizesNewlyObservedAutomationIdsRelatedToFailure()
    {
        var before = LocalTaskSelectorEvidence.Create(
        [
            CreateTree(
                "before-open",
                new JsonObject
                {
                    ["id"] = "open",
                    ["type"] = "MauiButton",
                    ["automationId"] = "map-open-filters-button",
                    ["visible"] = true,
                    ["children"] = new JsonArray()
                })
        ]);
        var after = LocalTaskSelectorEvidence.Create(
        [
            CreateTree(
                "after-open",
                new JsonObject
                {
                    ["id"] = "close",
                    ["type"] = "MauiButton",
                    ["automationId"] = "area-filters-close-button",
                    ["visible"] = true,
                    ["children"] = new JsonArray()
                },
                new JsonObject
                {
                    ["id"] = "title",
                    ["type"] = "MauiLabel",
                    ["text"] = "Area filters",
                    ["visible"] = true,
                    ["children"] = new JsonArray()
                })
        ]);
        var failedCall = Assert.Single(LocalTaskSelectorEvidence.ExtractSelectorCalls(
            "await ansight.ui.waitFor({ type: \"AreaFiltersSheet\", visible: true });"));

        var suggestions = LocalTaskSelectorEvidence.SuggestSelectors(failedCall, before, after);

        Assert.Equal(
            "area-filters-close-button",
            suggestions[0].Selector["automationId"]!.GetValue<string>());
        Assert.Contains(suggestions, suggestion => string.Equals(
            suggestion.Selector["text"]?.GetValue<string>(),
            "Area filters",
            StringComparison.Ordinal));
    }

    [Fact]
    public void FindSelectorCall_MapsRepeatedToolCallsByOccurrence()
    {
        var now = DateTimeOffset.UtcNow;
        RepositoryTaskToolCall[] toolCalls =
        [
            new(1, "ansight_wait_for_ui", now, 10, false, "ready"),
            new(2, "ansight_tap_ui", now.AddMilliseconds(20), 10, false, "tapped"),
            new(3, "ansight_wait_for_ui", now.AddMilliseconds(40), 10_000, true, "timed out")
        ];
        var selectorCalls = LocalTaskSelectorEvidence.ExtractSelectorCalls("""
            await ansight.ui.waitFor({ automationId: "map-open-filters-button" });
            await ansight.ui.tap({ automationId: "map-open-filters-button" });
            await ansight.ui.waitFor({ type: "AreaFiltersSheet" });
            """);

        var mapped = LocalTaskExtractionCoordinator.FindSelectorCall(
            toolCalls,
            toolCalls[2],
            selectorCalls);

        Assert.NotNull(mapped);
        Assert.Equal("AreaFiltersSheet", mapped.Fields["type"]);
    }

    private static SessionVisualTreeSnapshot CreateTree(
        string snapshotId,
        params JsonObject[] children)
        => new()
        {
            SnapshotId = snapshotId,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Source = "test",
            VisualTreeKind = "native",
            RuntimePlatform = "ios",
            NodeCount = children.Length + 1,
            Payload = new JsonObject
            {
                ["root"] = new JsonObject
                {
                    ["id"] = "root",
                    ["type"] = "UIWindow",
                    ["visible"] = true,
                    ["children"] = new JsonArray(children)
                }
            }
        };
}
