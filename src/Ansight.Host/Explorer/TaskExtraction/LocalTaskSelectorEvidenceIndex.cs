using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host.Models.Session;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Host.Runtime.Sanitization;

namespace Ansight.Host.Explorer.TaskExtraction;

internal sealed class LocalTaskSelectorEvidenceIndex
{
    public LocalTaskSelectorEvidenceIndex(
        int treeCount,
        int screenshotFrameCount,
        int ocrFrameCount,
        IReadOnlyList<LocalTaskSelectorEvidenceNode> nodes)
    {
        TreeCount = treeCount;
        ScreenshotFrameCount = screenshotFrameCount;
        OcrFrameCount = ocrFrameCount;
        Nodes = nodes;
    }

    public int TreeCount { get; }
    public int ScreenshotFrameCount { get; }
    public int OcrFrameCount { get; }
    public IReadOnlyList<LocalTaskSelectorEvidenceNode> Nodes { get; }

    public LocalTaskSelectorEvidenceIndex WithOcrBlocks(
        SessionImageFrame frame,
        IReadOnlyList<SessionScreenshotTextBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(blocks);
        var ocrNodes = blocks
            .Where(static block => !string.IsNullOrWhiteSpace(block.Text))
            .Select(block => new LocalTaskSelectorEvidenceNode(
                NodeId: null,
                AutomationId: null,
                Role: "text",
                Type: null,
                TextValues: new HashSet<string>([block.Text.Trim()], StringComparer.OrdinalIgnoreCase),
                Actions: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                AncestorAutomationIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                frame.FrameId,
                frame.CapturedAtUtc,
                VisualTreeKind: "screenshot",
                Source: "ocr"));
        return new LocalTaskSelectorEvidenceIndex(
            TreeCount,
            ScreenshotFrameCount,
            OcrFrameCount + 1,
            [.. Nodes, .. ocrNodes]);
    }

    public bool ContainsIdentity(LocalTaskSelectorEvidenceNode candidate)
        => Nodes.Any(node =>
            (!string.IsNullOrWhiteSpace(candidate.AutomationId)
             && string.Equals(node.AutomationId, candidate.AutomationId, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(candidate.NodeId)
                && string.Equals(node.NodeId, candidate.NodeId, StringComparison.OrdinalIgnoreCase))
            || (candidate.TextValues.Count > 0
                && candidate.TextValues.Overlaps(node.TextValues)
                && string.Equals(node.Role, candidate.Role, StringComparison.OrdinalIgnoreCase)
                && string.Equals(node.Type, candidate.Type, StringComparison.OrdinalIgnoreCase)));
}
