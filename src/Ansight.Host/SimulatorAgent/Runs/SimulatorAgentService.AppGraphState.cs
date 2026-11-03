using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent;

public sealed partial class SimulatorAgentService
{
    private static bool IsAppGraphObservationTool(string toolName)
        => toolName is "ansight_get_live_visual_tree" or "ansight_evaluate_screenshot"
            or "ansight_find_ui" or "ansight_wait_for_ui" or "ansight_assert_ui" or "ansight_scan_screen";

    private static bool IsAppGraphForegroundObservation(string toolName, JsonObject arguments)
    {
        if (toolName == "ansight_evaluate_screenshot")
        {
            return true;
        }

        // A focused match or OCR excerpt can describe content behind a sheet.
        // Only a rendered hierarchy or full-image assessment can establish the
        // foreground when correcting state without a delivered input.
        var isRenderedHierarchy = toolName == "ansight_get_live_visual_tree"
            && ReadString(arguments, "toolId") is "ui.get_visual_tree" or "device.accessibility"
            && NormalizeOptional(ReadString(arguments, "rootNodeId")) is null;
        return isRenderedHierarchy;
    }

    private static string? ValidateAppGraphStateObservation(
        string? currentDestinationId, SimulatorAgentAppGraphPendingOutcome? pendingOutcome,
        string? observationId, JsonObject report)
    {
        var nextDestinationId = NormalizeOptional(ReadString(report, "currentDestinationId"));
        var keepsCurrentState = currentDestinationId is null
            || pendingOutcome is not null
            || currentDestinationId == nextDestinationId;
        if (keepsCurrentState)
        {
            return null;
        }

        var aliases = report["destinationAliases"] as JsonArray;
        var explicitlyAliasesCurrentState = aliases?.OfType<JsonObject>().Any(alias =>
            ReadString(alias, "from") == currentDestinationId && ReadString(alias, "to") == nextDestinationId) == true;
        if (explicitlyAliasesCurrentState)
        {
            // The store validates alias identity and applies it atomically with this report.
            return null;
        }

        var evidence = report["stateObservation"] as JsonObject;
        var hasFreshCorrection = observationId is not null
            && evidence is not null
            && ReadString(evidence, "observationId") == observationId
            && NormalizeOptional(ReadString(evidence, "evidence")) is not null;
        if (hasFreshCorrection)
        {
            return null;
        }

        return $"The current destination remains '{currentDestinationId}'; no input established a change. "
            + "Updating another destination's coverage must retain currentDestinationId. "
            + "For a genuine state correction, inspect the full native/accessibility hierarchy or assess a fresh full screenshot, "
            + "then provide stateObservation with its fresh observationId and foreground evidence. "
            + "Focused matches and OCR excerpts cannot prove that a covering sheet or dialog is absent.";
    }
}
