using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class HostTimelineTaskExtractorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostTapsRecoverDownOnlyEvidenceAndDoNotDuplicateCompleteGestures(bool complete)
    {
        var start = DateTimeOffset.Parse("2026-10-18T00:00:00Z");
        var touches = Enumerable.Range(1, 3).SelectMany(i => complete
            ? new[] { CreateTouch("down", start.AddSeconds(i), .5, .5), CreateTouch("up", start.AddSeconds(i + .05), .5, .5) }
            : new[] { CreateTouch("down", start.AddSeconds(i), .5, .5) }).ToArray();
        var events = Enumerable.Range(1, 3).Select(i => new HostInputAction($"tap-{i}", "tap",
            start.AddSeconds(i), start.AddSeconds(i + .1), "succeeded", NormalizedX: .5, NormalizedY: .5).ToEvent()).ToArray();
        var snapshot = CreateSnapshot(start, touches,
            Enumerable.Range(1, 3).Select(i => CreateVisualTree(start.AddSeconds(i - .1), $"button-{i}", $"Button {i}")).ToArray(),
            hostEvents: events);
        var result = TimelineTaskExtractor.Extract(snapshot, start, start.AddSeconds(4), "Three injected taps");
        Assert.Equal(3, result.GeneratedActionCount);
        Assert.Equal(3, result.ReplaySteps.Count);
        Assert.DoesNotContain(result.Diagnostics, item => item.Contains("incomplete"));
        Assert.All(Enumerable.Range(1, 3), i => Assert.Contains($"button-{i}", result.Source));
    }

    [Fact]
    public void HostActionsDoNotHideOtherTouchesAndFailedOrPartialActionsAreNotReplayed()
    {
        var start = DateTimeOffset.Parse("2026-10-18T00:00:00Z");
        var snapshot = CreateSnapshot(start,
            [CreateTouch("down", start.AddSeconds(1), .5, .5),
             CreateTouch("up", start.AddSeconds(1.02), .5, .5),
             CreateTouch("down", start.AddSeconds(1.03), .5, .5),
             CreateTouch("up", start.AddSeconds(1.04), .5, .5),
             CreateTouch("down", start.AddSeconds(4.1), .5, .5),
             CreateTouch("up", start.AddSeconds(4.2), .5, .5)],
            [CreateVisualTree(start.AddSeconds(.9), "button", "Button")], hostEvents:
            [new HostInputAction("tap", "tap", start.AddSeconds(1), start.AddSeconds(1.1), "succeeded", NormalizedX: .5, NormalizedY: .5).ToEvent(),
             new HostInputAction("failed", "keyboard.submit", start.AddSeconds(2), start.AddSeconds(2.1), "failed").ToEvent(),
             new HostInputAction("cancelled", "keyboard.press", start.AddSeconds(3), start.AddSeconds(3.1), "interrupted").ToEvent(),
             new HostInputAction("partial", "keyboard.submit", start.AddSeconds(4), start.AddSeconds(5), "succeeded").ToEvent(),
             new HostInputAction("partial-tap", "tap", start.AddSeconds(4), start.AddSeconds(5), "succeeded", NormalizedX: .5, NormalizedY: .5).ToEvent()]);
        var result = TimelineTaskExtractor.Extract(snapshot, start, start.AddSeconds(4.5), "Evidence boundaries");
        Assert.Equal(2, result.GeneratedActionCount);
        Assert.DoesNotContain("ansight.keyboard", result.Source);
        Assert.Contains(result.Diagnostics, item => item.Contains("failed"));
        Assert.Contains(result.Diagnostics, item => item.Contains("interrupted"));
        Assert.Contains(result.Diagnostics, item => item.Contains("range boundary"));
    }

    [Fact]
    public void KeyboardActionsRetainOrderAndDoNotBecomeTapsOrTextFromTheirEffects()
    {
        var start = DateTimeOffset.Parse("2026-10-18T00:00:00Z");
        var cleared = CreateInputVisualTree(start.AddSeconds(3.2), "after-submit", "",
            actionId: "submit-evidence", phase: "after");
        var snapshot = CreateSnapshot(start, [],
            [CreateInputVisualTree(start.AddSeconds(.1), "empty", ""),
             CreateInputVisualTree(start.AddSeconds(1), "typed", "purple cubes"), cleared], hostEvents:
            [new HostInputAction("enter", "keyboard.press", start.AddSeconds(2), start.AddSeconds(2.1), "succeeded").ToEvent(),
             new HostInputAction("submit", "keyboard.submit", start.AddSeconds(3), start.AddSeconds(3.1), "succeeded", "submit-evidence").ToEvent()]);
        var result = TimelineTaskExtractor.Extract(snapshot, start, start.AddSeconds(4), "Submit search");
        Assert.Equal(new[] { "input", "keyboard.press", "keyboard.submit" }, result.Actions.Select(a => a.Kind));
        Assert.Contains("ansight.keyboard.press({ key: \"Enter\" })", result.Source);
        Assert.Contains("ansight.keyboard.submit()", result.Source);
        Assert.DoesNotContain("clear", result.ReplayInstructions.Last());
        var test = WorkspaceTestExtractor.Extract(snapshot, start, start.AddSeconds(4), "Submit search", ["Results appear"]);
        Assert.Contains("submit action", test.Source);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Search products…")]
    [InlineData("  query\n")]
    public void ActualValuesIncludingEmptyOrPlaceholderMatchingTextRemainDistinct(string value)
    {
        var node = new JsonObject { ["label"] = "Search products…", ["visual"] = new JsonObject
            { ["text"] = "Search products…", ["value"] = value } };
        Assert.Equal(value, LiveUiNodeQuery.ReadValue(node));
        node["visual"]!.AsObject().Remove("value");
        Assert.Null(LiveUiNodeQuery.ReadValue(node));
    }

    [Fact]
    public void PlaceholderChangesAloneNeverGenerateInput()
    {
        var start = DateTimeOffset.Parse("2026-10-18T00:00:00Z");
        var before = CreateInputVisualTree(start.AddSeconds(1), "before", "", label: "Search products…");
        var after = CreateInputVisualTree(start.AddSeconds(2), "after", "", label: "Search again…");
        var result = TimelineTaskExtractor.Extract(CreateSnapshot(start, [], [before, after]), start, start.AddSeconds(3), "Empty search");
        Assert.Equal(0, result.GeneratedActionCount);
        Assert.DoesNotContain("ansight.ui.typeText", result.Source);
    }

    [Fact]
    public void ValueObservedAfterEnterIsOrderedBeforeItAndRedactedValuesAreOmitted()
    {
        var start = DateTimeOffset.Parse("2026-10-18T00:00:00Z");
        var snapshot = CreateSnapshot(start, [],
            [CreateInputVisualTree(start.AddSeconds(.1), "empty", ""),
             CreateInputVisualTree(start.AddSeconds(2.2), "typed", "purple cubes"),
             CreateInputVisualTree(start.AddSeconds(3), "redacted", "[redacted]")], hostEvents:
            [new HostInputAction("enter", "keyboard.press", start.AddSeconds(2), start.AddSeconds(2.1), "succeeded").ToEvent()]);
        var result = TimelineTaskExtractor.Extract(snapshot, start, start.AddSeconds(4), "Enter search");
        Assert.Equal(new[] { "input", "keyboard.press" }, result.Actions.Select(action => action.Kind));
        Assert.Contains(result.Diagnostics, message => message.Contains("masked"));
        Assert.DoesNotContain("[redacted]", result.Source);
    }
}
