using System.Text.Json.Nodes;

namespace Ansight.Host.AppGraphs;

internal static class LocalAppGraphNavigationDiscovery
{
    public static async Task<LocalAppGraphNavigationDiscoveryResult> DiscoverAsync(
        AppToolService appTools,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(appTools);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new(false, "Choose a connected live session.", []);
        }

        var normalizedSessionId = sessionId.Trim();
        var catalogResponse = await appTools.QueryForLocalExplorerAsync(
                normalizedSessionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!catalogResponse.Success || catalogResponse.Envelope is null)
        {
            return new(false, catalogResponse.Message, []);
        }

        var available = GetLiveNavigationStructureTool.BuildAvailableNavigationControllers(
            catalogResponse.Envelope.Payload,
            preferredFramework: null);
        var controllers = new List<LocalAppGraphNavigationController>();
        var failures = new List<string>();
        foreach (var value in available.OfType<JsonObject>())
        {
            var framework = ReadString(value, "framework") ?? string.Empty;
            var toolId = ReadString(value, "navigationToolId") ?? string.Empty;
            var guidance = ReadString(value, "guidance") ?? string.Empty;
            var response = await appTools.CallForLocalExplorerAsync(
                    normalizedSessionId,
                    toolId,
                    new JsonObject(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!response.Success || response.Envelope is null)
            {
                failures.Add($"{framework}: {response.Message}");
                continue;
            }

            var observation = NavigationStructureObservation.Build(new JsonObject
            {
                ["sessionId"] = normalizedSessionId,
                ["toolId"] = toolId,
                ["selectedNavigationController"] = framework,
                ["availableNavigationControllers"] = available.DeepClone(),
                ["payload"] = response.Envelope.Payload?.DeepClone()
            });
            var fingerprint = observation is null
                ? string.Empty
                : ReadString(observation, "structureFingerprint") ?? string.Empty;
            if (fingerprint.Length == 0)
            {
                failures.Add($"{framework}: the navigation-state response could not be fingerprinted.");
                continue;
            }

            var frameworkDefinition = AppGraphNavigationTechnologyCatalog.Frameworks.FirstOrDefault(candidate =>
                string.Equals(candidate.Framework, framework, StringComparison.Ordinal));
            controllers.Add(new LocalAppGraphNavigationController(
                framework,
                frameworkDefinition?.Label ?? framework,
                toolId,
                fingerprint,
                guidance,
                frameworkDefinition?.Kinds ?? []));
        }

        foreach (var controller in NavigationControllerCatalog.All)
        {
            if (controllers.Any(candidate => string.Equals(
                    candidate.Framework,
                    controller.Framework,
                    StringComparison.Ordinal))
                || !controller.VisualTreeToolIds.Any(toolId =>
                    !string.Equals(toolId, VisualTreeContract.NativeToolId, StringComparison.Ordinal)
                    && RemoteAppToolCatalog.HasTool(catalogResponse.Envelope.Payload, toolId)))
            {
                continue;
            }

            var frameworkDefinition = AppGraphNavigationTechnologyCatalog.Frameworks.FirstOrDefault(candidate =>
                string.Equals(candidate.Framework, controller.Framework, StringComparison.Ordinal));
            if (frameworkDefinition is null)
            {
                continue;
            }
            controllers.Add(new LocalAppGraphNavigationController(
                controller.Framework,
                frameworkDefinition.Label,
                string.Empty,
                string.Empty,
                NavigationGuidance.Read(controller.Framework),
                frameworkDefinition.Kinds));
        }

        if (controllers.Count > 0)
        {
            var suffix = failures.Count == 0
                ? string.Empty
                : $" {failures.Count} additional controller(s) could not be observed.";
            return new(
                true,
                $"Resolved {controllers.Count} framework-specific navigation topology source(s).{suffix}",
                controllers);
        }

        var message = failures.Count > 0
            ? string.Join(" ", failures)
            : "The connected app does not expose a supported framework navigation-state controller.";
        return new(false, message, []);
    }

    private static string? ReadString(JsonObject source, string propertyName)
        => source[propertyName] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;
}
