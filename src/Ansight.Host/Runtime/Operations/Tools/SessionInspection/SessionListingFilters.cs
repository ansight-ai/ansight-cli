using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal static class SessionListingFilters
{
    public static void AddProperties(Dictionary<string, ToolSchema> properties)
    {
        properties["platform"] = SessionInspectionToolSchemas.PlatformSchema("Optional platform key filter.");
        properties["platforms"] = SessionInspectionToolSchemas.PlatformsSchema();
        properties["deviceFormFactor"] = SessionInspectionToolSchemas.DeviceFormFactorSchema("Optional device form-factor filter.");
        properties["deviceFormFactors"] = SessionInspectionToolSchemas.DeviceFormFactorsSchema("Optional device form-factor filters.");
        properties["formFactor"] = SessionInspectionToolSchemas.DeviceFormFactorSchema("Alias for deviceFormFactor.");
        properties["formFactors"] = SessionInspectionToolSchemas.DeviceFormFactorsSchema("Alias for deviceFormFactors.");
        properties["idiom"] = SessionInspectionToolSchemas.DeviceFormFactorSchema("Alias for deviceFormFactor.");
        properties["idioms"] = SessionInspectionToolSchemas.DeviceFormFactorsSchema("Alias for deviceFormFactors.");
        properties["osName"] = ToolSchema.String("Optional operating-system family/name filter, matched exactly case-insensitively.", nullable: true);
        properties["osNames"] = ToolSchema.Array(
            ToolSchema.String("Operating-system family/name filter, matched exactly case-insensitively."),
            description: "Optional operating-system family/name filters.",
            nullable: true);
        properties["osVersion"] = ToolSchema.String("Optional operating-system version filter, matched exactly case-insensitively.", nullable: true);
        properties["osVersions"] = ToolSchema.Array(
            ToolSchema.String("Operating-system version filter, matched exactly case-insensitively."),
            description: "Optional operating-system version filters.",
            nullable: true);
        properties["deviceType"] = SessionInspectionToolSchemas.DeviceTypeSchema("Optional physical, virtual, emulator, or simulator filter.");
        properties["deviceTypes"] = SessionInspectionToolSchemas.DeviceTypesSchema("Optional physical, virtual, emulator, or simulator filters.");
        properties["isVirtual"] = ToolSchema.Boolean("Filter by virtual-device state. True includes virtual devices, emulators, and simulators; false includes physical devices.", nullable: true);
        properties["isEmulator"] = ToolSchema.Boolean("Filter by emulator/simulator state.", nullable: true);
        properties["deviceClassCode"] = ToolSchema.Integer("Optional device class code filter.", nullable: true);
        properties["deviceClassCodes"] = ToolSchema.Array(
            ToolSchema.Integer("Device class code."),
            description: "Optional device class code filters.",
            nullable: true);
    }

    public static SessionListingFilterCriteria Read(JsonObject? arguments)
    {
        var criteria = new SessionListingFilterCriteria
        {
            Platforms = SessionPlatformFilters.Read(arguments),
            IsVirtual = arguments?["isVirtual"]?.GetValue<bool>(),
            IsEmulator = arguments?["isEmulator"]?.GetValue<bool>()
        };

        AddStringFilter(criteria.DeviceFormFactors, arguments?["deviceFormFactor"]?.GetValue<string>());
        AddStringFilters(criteria.DeviceFormFactors, arguments?["deviceFormFactors"] as JsonArray);
        AddStringFilter(criteria.DeviceFormFactors, arguments?["formFactor"]?.GetValue<string>());
        AddStringFilters(criteria.DeviceFormFactors, arguments?["formFactors"] as JsonArray);
        AddStringFilter(criteria.DeviceFormFactors, arguments?["idiom"]?.GetValue<string>());
        AddStringFilters(criteria.DeviceFormFactors, arguments?["idioms"] as JsonArray);
        AddStringFilter(criteria.OsNames, arguments?["osName"]?.GetValue<string>());
        AddStringFilters(criteria.OsNames, arguments?["osNames"] as JsonArray);
        AddStringFilter(criteria.OsVersions, arguments?["osVersion"]?.GetValue<string>());
        AddStringFilters(criteria.OsVersions, arguments?["osVersions"] as JsonArray);
        AddDeviceTypeFilter(criteria.DeviceTypes, arguments?["deviceType"]?.GetValue<string>());
        AddDeviceTypeFilters(criteria.DeviceTypes, arguments?["deviceTypes"] as JsonArray);
        AddIntegerFilter(criteria.DeviceClassCodes, arguments?["deviceClassCode"]?.GetValue<int?>());
        AddIntegerFilters(criteria.DeviceClassCodes, arguments?["deviceClassCodes"] as JsonArray);

        return criteria;
    }

    public static bool Matches(AppSessionSnapshot snapshot, SessionListingFilterCriteria criteria)
    {
        if (criteria.Platforms.Count > 0
            && !criteria.Platforms.Contains(SessionPlatformFilters.ResolveSessionPlatformKey(snapshot)))
        {
            return false;
        }

        var device = snapshot.DeviceProfile?.Device;
        if (!MatchesStringSet(device?.FormFactor, criteria.DeviceFormFactors)
            || !MatchesStringSet(device?.OsName, criteria.OsNames)
            || !MatchesStringSet(device?.OsVersion, criteria.OsVersions)
            || !MatchesIntegerSet(device?.DeviceClassCode, criteria.DeviceClassCodes))
        {
            return false;
        }

        var isVirtual = ResolveVirtualDeviceState(snapshot);
        if (criteria.IsVirtual.HasValue && isVirtual != criteria.IsVirtual.Value)
        {
            return false;
        }

        var isEmulator = device?.IsEmulator;
        if (criteria.IsEmulator.HasValue && isEmulator != criteria.IsEmulator.Value)
        {
            return false;
        }

        return MatchesDeviceTypes(snapshot, criteria.DeviceTypes);
    }

    public static JsonObject BuildPayload(SessionListingFilterCriteria criteria)
    {
        return new JsonObject
        {
            ["platforms"] = PayloadJson.CreateJsonArray(criteria.Platforms.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value))),
            ["deviceFormFactors"] = PayloadJson.CreateJsonArray(criteria.DeviceFormFactors.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value))),
            ["osNames"] = PayloadJson.CreateJsonArray(criteria.OsNames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value))),
            ["osVersions"] = PayloadJson.CreateJsonArray(criteria.OsVersions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value))),
            ["deviceTypes"] = PayloadJson.CreateJsonArray(criteria.DeviceTypes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value))),
            ["isVirtual"] = criteria.IsVirtual,
            ["isEmulator"] = criteria.IsEmulator,
            ["deviceClassCodes"] = PayloadJson.CreateJsonArray(criteria.DeviceClassCodes.OrderBy(value => value).Select(value => JsonValue.Create(value)))
        };
    }

    private static void AddStringFilters(HashSet<string> filters, JsonArray? array)
    {
        if (array is null)
        {
            return;
        }

        foreach (var item in array)
        {
            AddStringFilter(filters, item?.GetValue<string>());
        }
    }

    private static void AddStringFilter(HashSet<string> filters, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            filters.Add(value.Trim());
        }
    }

    private static void AddDeviceTypeFilters(HashSet<string> filters, JsonArray? array)
    {
        if (array is null)
        {
            return;
        }

        foreach (var item in array)
        {
            AddDeviceTypeFilter(filters, item?.GetValue<string>());
        }
    }

    private static void AddDeviceTypeFilter(HashSet<string> filters, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.Trim();
        if (string.Equals(normalized, "sim", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "simulator";
        }

        filters.Add(normalized);
    }

    private static void AddIntegerFilters(HashSet<int> filters, JsonArray? array)
    {
        if (array is null)
        {
            return;
        }

        foreach (var item in array)
        {
            AddIntegerFilter(filters, item?.GetValue<int?>());
        }
    }

    private static void AddIntegerFilter(HashSet<int> filters, int? value)
    {
        if (value.HasValue)
        {
            filters.Add(value.Value);
        }
    }

    private static bool MatchesStringSet(string? value, HashSet<string> filters)
    {
        return filters.Count == 0
               || (!string.IsNullOrWhiteSpace(value) && filters.Contains(value.Trim()));
    }

    private static bool MatchesIntegerSet(int? value, HashSet<int> filters)
    {
        return filters.Count == 0
               || (value.HasValue && filters.Contains(value.Value));
    }

    private static bool MatchesDeviceTypes(AppSessionSnapshot snapshot, HashSet<string> filters)
    {
        if (filters.Count == 0)
        {
            return true;
        }

        var isVirtual = ResolveVirtualDeviceState(snapshot);
        var isEmulator = snapshot.DeviceProfile?.Device?.IsEmulator;
        return filters.Any(filter => filter.ToLowerInvariant() switch
        {
            "physical" => isVirtual == false,
            "virtual" => isVirtual == true,
            "emulator" => isEmulator == true,
            "simulator" => isEmulator == true,
            _ => false
        });
    }

    private static bool? ResolveVirtualDeviceState(AppSessionSnapshot snapshot)
    {
        var device = snapshot.DeviceProfile?.Device;
        return device?.IsVirtual ?? device?.IsEmulator;
    }
}
