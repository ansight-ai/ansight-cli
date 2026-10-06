using System.Globalization;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DeviceLocation;

internal sealed class SetDeviceLocationTool : DeviceHostTargetOperation
{
    public SetDeviceLocationTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_set_device_location";

    protected override string Title => "Set Device Location";

    protected override string Description =>
        "Set an iOS Simulator or Android emulator location through Ansight using simctl or ADB.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: BuildProperties(),
        required: ["latitude", "longitude"],
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryReadCoordinate(arguments, "latitude", -90, 90, out var latitude, out var coordinateError)
            || !TryReadCoordinate(arguments, "longitude", -180, 180, out var longitude, out coordinateError))
        {
            return ToolError(coordinateError);
        }

        if (!TryResolveTarget(arguments, out var target, out var targetError))
        {
            return ToolError(targetError);
        }

        var result = await deviceLocationRouter.SetLocationAsync(
            new SetDeviceLocationRequest(target.DeviceIdentifier, latitude, longitude),
            CancellationToken.None);
        var payload = BuildResultPayload("set", target, result);
        payload["latitude"] = latitude;
        payload["longitude"] = longitude;
        return RequestResult.ToolResult(payload, isError: !result.IsSuccess);
    }

    private static Dictionary<string, ToolSchema> BuildProperties()
    {
        var properties = BuildTargetProperties();
        properties["latitude"] = ToolSchema.Number("Latitude from -90 through 90 degrees.");
        properties["longitude"] = ToolSchema.Number("Longitude from -180 through 180 degrees.");
        return properties;
    }

    private static bool TryReadCoordinate(
        JsonObject? arguments,
        string propertyName,
        double minimum,
        double maximum,
        out double value,
        out string error)
    {
        value = 0;
        error = string.Empty;
        if (arguments?[propertyName] is not JsonValue jsonValue
            || (!jsonValue.TryGetValue<double>(out value)
                && !double.TryParse(
                    jsonValue.ToString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value)))
        {
            error = $"{propertyName} must be a number.";
            return false;
        }

        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            error = $"{propertyName} must be between {minimum} and {maximum}.";
            return false;
        }

        return true;
    }
}
