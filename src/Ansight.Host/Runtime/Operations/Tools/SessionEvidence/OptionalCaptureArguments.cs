using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal static class OptionalCaptureArguments
{
    internal static bool TryApplyOptionalInteger(
        JsonObject? arguments,
        JsonObject target,
        string propertyName,
        int minimum,
        int maximum,
        out string? errorMessage)
    {
        errorMessage = null;

        if (arguments?[propertyName] is null)
        {
            return true;
        }

        if (arguments[propertyName] is JsonValue jsonValue
            && jsonValue.TryGetValue<int>(out var value))
        {
            var isWithinRange = value >= minimum && value <= maximum;

            if (isWithinRange)
            {
                target[propertyName] = value;

                return true;
            }
        }

        errorMessage = $"{propertyName} must be an integer between {minimum} and {maximum}.";

        return false;
    }
}
