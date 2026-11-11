using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed partial class LiveUiTreeCapture
{
    private static async Task<LiveUiCaptureResult> CaptureAvaloniaFallbackAsync(
        LiveUiCaptureResult preferred,
        AppSessionSnapshot session,
        IAppToolBridge bridge,
        JsonNode? catalog,
        AppToolBridgeRequestContext context,
        LiveUiSelector selector,
        CancellationToken cancellationToken)
    {
        var selected = preferred;
        var attempted = preferred.AttemptedToolIds.ToList();

        foreach (var toolId in new[]
        {
            VisualTreeContract.AvaloniaAccessibilityToolId,
            VisualTreeContract.AvaloniaLogicalToolId,
            VisualTreeContract.AvaloniaVisualToolId
        })
        {
            if (ContainsSelectorEvidence(selected, selector))
            {
                break;
            }

            if (attempted.Contains(toolId) || !RemoteAppToolCatalog.HasTool(catalog, toolId))
            {
                continue;
            }

            attempted.Add(toolId);
            var candidate = await CaptureWithToolAsync(
                session, bridge, toolId, context, cancellationToken).ConfigureAwait(false);

            if (!selected.IsSuccess || ContainsSelectorEvidence(candidate, selector))
            {
                selected = candidate;
            }
        }

        return selected with { AttemptedToolIds = attempted.ToArray() };
    }
}
