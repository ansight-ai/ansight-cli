using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Test;

internal static class WorkspaceTestTargetOptions
{
    public static WorkspaceTestTargetRequest? Resolve(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var targets = ResolveMany(arguments);
        if (targets.Count > 1)
        {
            throw new CliUsageException(
                "Pass only one device identifier for this command using --device-id or --device.");
        }

        return targets.SingleOrDefault();
    }

    public static IReadOnlyList<WorkspaceTestTargetRequest> ResolveMany(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var headless = arguments.HasFlag("headless");
        var executionMode = ResolveExecutionMode(arguments);
        if (executionMode == WorkspaceExecutionModes.Device && arguments.HasFlag("app-graph"))
            throw new CliUsageException("App Graph execution requires SDK mode in v1.");
        var applicationPath = ResolveApplicationPath(arguments);
        var platform = arguments.HasFlag("platform")
            ? arguments.RequireOption("platform")
            : null;
        var deviceIdentifiers = ResolveDeviceIdentifiers(arguments);
        var deviceKind = arguments.HasFlag("device-kind")
            ? arguments.RequireOption("device-kind")
            : null;
        if (applicationPath is null
            && platform is null
            && deviceIdentifiers.Count == 0
            && deviceKind is null
            && executionMode == WorkspaceExecutionModes.Sdk
            && !headless)
        {
            return [];
        }

        return deviceIdentifiers.Count == 0
            ? [new WorkspaceTestTargetRequest(platform, null, applicationPath, deviceKind, headless) { ExecutionMode = executionMode }]
            : deviceIdentifiers.Select(deviceIdentifier => new WorkspaceTestTargetRequest(
                platform,
                deviceIdentifier,
                applicationPath,
                deviceKind,
                headless) { ExecutionMode = executionMode }).ToArray();
    }

    internal static string ResolveExecutionMode(CliArguments arguments)
    {
        if (!arguments.HasFlag("execution-mode")) return WorkspaceExecutionModes.Sdk;
        try { return WorkspaceExecutionModes.Normalize(arguments.RequireOption("execution-mode")); }
        catch (ArgumentException exception) { throw new CliUsageException(exception.Message); }
    }

    private static IReadOnlyList<string> ResolveDeviceIdentifiers(CliArguments arguments)
    {
        var values = new List<string>();
        AddOptionValues(arguments, "device-id", values);
        AddOptionValues(arguments, "device", values);
        return values
            .Select(static value => value.Trim())
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ResolveApplicationPath(CliArguments arguments)
    {
        var values = new List<string>();
        AddOptionValue(arguments, "app", values);
        AddOptionValue(arguments, "ipa", values);
        AddOptionValue(arguments, "application-path", values);
        var distinctValues = values.Distinct(StringComparer.Ordinal).ToArray();
        if (distinctValues.Length > 1)
        {
            throw new CliUsageException(
                "Pass only one application artifact using --app, --ipa, or --application-path.");
        }

        if (arguments.HasFlag("ipa")
            && distinctValues.Length == 1
            && !string.Equals(Path.GetExtension(distinctValues[0]), ".ipa", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException("--ipa requires a path ending in .ipa.");
        }

        return distinctValues.SingleOrDefault();
    }

    private static void AddOptionValue(
        CliArguments arguments,
        string optionName,
        ICollection<string> values)
    {
        if (arguments.HasFlag(optionName))
        {
            values.Add(arguments.RequireOption(optionName));
        }
    }

    private static void AddOptionValues(
        CliArguments arguments,
        string optionName,
        ICollection<string> values)
    {
        foreach (var value in arguments.GetOptions(optionName))
        {
            values.Add(value);
        }
    }
}
