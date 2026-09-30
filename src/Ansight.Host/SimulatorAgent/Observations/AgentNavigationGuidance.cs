using System.Text.Json;

namespace Ansight.Host.SimulatorAgent.Observations;

internal sealed class AgentNavigationGuidance
{
    private readonly HashSet<string> retainedFrameworks = new(StringComparer.Ordinal);

    public string TakeNewGuidance(string observationOutput)
    {
        if (string.IsNullOrWhiteSpace(observationOutput))
        {
            return string.Empty;
        }

        var observedFrameworks = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(observationOutput);
            CollectObservationFrameworks(document.RootElement, observedFrameworks);
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        var sections = new List<string>();
        foreach (var framework in observedFrameworks)
        {
            if (retainedFrameworks.Add(framework))
            {
                sections.Add($"Navigation guidance for {framework}:\n{NavigationGuidance.Read(framework)}");
            }
        }
        return string.Join("\n\n", sections);
    }

    private static void CollectObservationFrameworks(
        JsonElement result,
        ISet<string> frameworks)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var controllers = new SortedSet<string>(StringComparer.Ordinal);
        AddFramework(ReadString(result, "selectedNavigationController"), controllers);
        CollectControllers(result, "graphStructureControllers", controllers);
        CollectControllers(result, "availableNavigationControllers", controllers);
        if (controllers.Count > 0)
        {
            // Explicit controller identities are more precise than a fallback framework label.
            frameworks.UnionWith(controllers);
        }
        else if (ReadString(result, "capability") is "ui.observe" or "navigation.structure.observe")
        {
            AddFramework(ReadString(result, "framework"), frameworks);
            if (result.TryGetProperty("frameworks", out var frameworkValues)
                && frameworkValues.ValueKind == JsonValueKind.Array)
            {
                foreach (var framework in frameworkValues.EnumerateArray())
                {
                    if (framework.ValueKind == JsonValueKind.String)
                    {
                        AddFramework(framework.GetString(), frameworks);
                    }
                }
            }
        }

        // Traverse result envelopes only: app state and node values are never instruction sources.
        foreach (var propertyName in new[] { "result", "payload", "afterObservation" })
        {
            if (result.TryGetProperty(propertyName, out var observation))
            {
                CollectObservationFrameworks(observation, frameworks);
            }
        }
        foreach (var propertyName in new[] { "actions", "steps", "results" })
        {
            if (result.TryGetProperty(propertyName, out var observations)
                && observations.ValueKind == JsonValueKind.Array)
            {
                foreach (var observation in observations.EnumerateArray())
                {
                    CollectObservationFrameworks(observation, frameworks);
                }
            }
        }
    }

    private static void CollectControllers(
        JsonElement observation,
        string propertyName,
        ISet<string> frameworks)
    {
        if (!observation.TryGetProperty(propertyName, out var controllers)
            || controllers.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var controller in controllers.EnumerateArray())
        {
            AddFramework(ReadString(controller, "framework"), frameworks);
        }
    }

    private static void AddFramework(string? framework, ISet<string> frameworks)
    {
        if (NavigationControllerCatalog.ResolveFramework(framework) is { } controller)
        {
            frameworks.Add(controller.Framework);
        }
    }

    private static string? ReadString(JsonElement value, string propertyName)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
