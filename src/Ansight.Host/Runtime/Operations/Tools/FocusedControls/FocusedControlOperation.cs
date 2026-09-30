using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.FocusedControls;

internal abstract class FocusedControlOperation : Operation
{
    protected FocusedControlOperation(OperationServices services)
        : base(services)
    {
    }

    protected JsonObject BuildListFocusedControlsPayload(JsonObject? arguments)
    {
        var focusedControls = GetFilteredFocusedControls(arguments)
            .Select(snapshot => (JsonNode?)snapshot.ToJson())
            .ToArray();

        return new JsonObject
        {
            ["count"] = focusedControls.Length,
            ["focusedControls"] = PayloadJson.CreateJsonArray(focusedControls)
        };
    }

    protected RequestResult BuildGetFocusedControlResult(JsonObject? arguments)
    {
        var focusedControls = GetFilteredFocusedControls(arguments).ToArray();
        if (focusedControls.Length == 0)
        {
            return ToolError("No focused visual tree control is available. Use ansight_find_ui with sessionId and a selector to discover an exact live target.");
        }

        if (focusedControls.Length > 1)
        {
            return RequestResult.ToolResult(
                new JsonObject
                {
                    ["message"] = $"Multiple focused controls are available. Provide sessionId or appId. Sessions: {string.Join(", ", focusedControls.Select(snapshot => snapshot.SessionId))}",
                    ["count"] = focusedControls.Length,
                    ["focusedControls"] = PayloadJson.CreateJsonArray(focusedControls.Select(snapshot => (JsonNode?)snapshot.ToJson()))
                },
                isError: true);
        }

        var focusedControl = focusedControls[0];
        return RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = $"Focused control '{focusedControl.Type}' loaded.",
                ["focusedControl"] = focusedControl.ToJson()
            },
            isError: false);
    }

    private IEnumerable<RuntimeFocusedControlSnapshot> GetFilteredFocusedControls(JsonObject? arguments)
    {
        var sessionId = NormalizeOptionalString(arguments?["sessionId"]?.GetValue<string>());
        var appId = NormalizeOptionalString(arguments?["appId"]?.GetValue<string>());
        return runtimeState.GetFocusedControlSnapshots()
            .Where(snapshot => sessionId is null || string.Equals(snapshot.SessionId, sessionId, StringComparison.Ordinal))
            .Where(snapshot => appId is null || string.Equals(snapshot.AppId, appId, StringComparison.Ordinal))
            .OrderByDescending(snapshot => snapshot.FocusedAtUtc);
    }
}
