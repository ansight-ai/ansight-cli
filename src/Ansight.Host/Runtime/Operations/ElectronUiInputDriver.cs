using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations;

// Routes only SDK-identified Electron sessions; native devices keep their existing driver.
internal sealed class ElectronUiInputDriver(
    IRuntimeState state, IAppToolBridge bridge, IUiInputDriver? fallback) : IUiInputDriver, IUiInputPreflightDriver
{
    internal const string Prefix = "electron:";
    internal static bool IsElectron(string id) => id.StartsWith(Prefix, StringComparison.Ordinal);

    private bool Matches(string sessionId, string deviceId) => state.GetSessionSummaries().Any(s =>
        s.SessionId == sessionId && s.DeviceProfile?.Sdk?.PackageId == "@ansight/electron"
        && s.DeviceProfile.Device?.NativeDeviceId == deviceId && bridge.IsSessionConnected(sessionId));

    public UiInputAvailability GetAvailability(string id) => IsElectron(id)
        ? new(state.GetSessionSummaries().Any(s => Matches(s.SessionId, id)), "electron-sdk", "Input is delivered through the attached Electron window.")
        : fallback?.GetAvailability(id) ?? UiInputAvailability.Unavailable("No native input driver is configured.");

    public Task<UiInputResult> PrepareForInputAsync(UiInputPreflightRequest r, CancellationToken ct = default) => IsElectron(r.DeviceIdentifier)
        ? Task.FromResult(Matches(r.SessionId, r.DeviceIdentifier)
            ? new UiInputResult(true, "electron-sdk", "Electron session is connected.")
            : UiInputResult.Failure("Electron input target does not match the connected SDK session."))
        : fallback is IUiInputPreflightDriver preflight ? preflight.PrepareForInputAsync(r, ct)
        : Task.FromResult(new UiInputResult(true, "", "No preflight required."));

    private async Task<UiInputResult> Send(string sessionId, string id, JsonObject args, CancellationToken ct)
    {
        if (!Matches(sessionId, id)) { return UiInputResult.Failure("Electron input target does not match the connected SDK session."); }
        var response = await bridge.CallToolWithCatalogPreflightAndRecoveryAsync(sessionId, "electron.input", args, null, ct,
            new AppToolBridgeRequestContext("host-ui", "electron.input", null));
        var payload = response.Envelope?.Payload;
        return response.Success && payload?["success"]?.GetValue<bool>() == true && payload?["result"]?["performed"]?.GetValue<bool>() == true
            ? new UiInputResult(true, "electron-sdk", "Input delivered to the Electron window.")
            : UiInputResult.Failure(payload?["message"]?.GetValue<string>() ?? response.Message);
    }

    private static JsonObject Point(string kind, double x, double y) => new() { ["kind"] = kind, ["x"] = x, ["y"] = y, ["normalized"] = true };
    private static Task<UiInputResult> Unavailable() => Task.FromResult(UiInputResult.Failure("No native input driver is configured."));
    public Task<UiInputResult> TapAsync(UiTapRequest r, CancellationToken ct = default) => IsElectron(r.DeviceIdentifier)
        ? Send(r.SessionId, r.DeviceIdentifier, Point("tap", r.NormalizedX, r.NormalizedY), ct) : fallback?.TapAsync(r, ct) ?? Unavailable();
    public Task<UiInputResult> LongPressAsync(UiLongPressRequest r, CancellationToken ct = default)
    {
        if (!IsElectron(r.DeviceIdentifier)) { return fallback?.LongPressAsync(r, ct) ?? Unavailable(); }
        r.Validate(); var args = Point("longPress", r.NormalizedX, r.NormalizedY); args["durationMs"] = r.DurationMilliseconds;
        return Send(r.SessionId, r.DeviceIdentifier, args, ct);
    }
    public Task<UiInputResult> SwipeAsync(UiSwipeRequest r, CancellationToken ct = default)
    {
        if (!IsElectron(r.DeviceIdentifier)) { return fallback?.SwipeAsync(r, ct) ?? Unavailable(); }
        var args = Point("swipe", r.StartNormalizedX, r.StartNormalizedY);
        args["endX"] = r.EndNormalizedX; args["endY"] = r.EndNormalizedY; args["durationMs"] = r.DurationMilliseconds;
        return Send(r.SessionId, r.DeviceIdentifier, args, ct);
    }
    public Task<UiInputResult> PinchAsync(UiPinchRequest r, CancellationToken ct = default)
    {
        if (!IsElectron(r.DeviceIdentifier)) { return fallback?.PinchAsync(r, ct) ?? Unavailable(); }
        var args = Point("pinch", (r.PrimaryStartNormalizedX + r.SecondaryStartNormalizedX) / 2, (r.PrimaryStartNormalizedY + r.SecondaryStartNormalizedY) / 2);
        args["primaryStartX"] = r.PrimaryStartNormalizedX; args["primaryStartY"] = r.PrimaryStartNormalizedY;
        args["secondaryStartX"] = r.SecondaryStartNormalizedX; args["secondaryStartY"] = r.SecondaryStartNormalizedY;
        args["primaryEndX"] = r.PrimaryEndNormalizedX; args["primaryEndY"] = r.PrimaryEndNormalizedY;
        args["secondaryEndX"] = r.SecondaryEndNormalizedX; args["secondaryEndY"] = r.SecondaryEndNormalizedY;
        args["durationMs"] = r.DurationMilliseconds;
        return Send(r.SessionId, r.DeviceIdentifier, args, ct);
    }
    public Task<UiInputResult> TypeTextAsync(UiTextRequest r, CancellationToken ct = default) => IsElectron(r.DeviceIdentifier)
        ? Send(r.SessionId, r.DeviceIdentifier, new() { ["kind"] = "type", ["text"] = r.Text, ["replaceExisting"] = r.ReplaceExisting }, ct)
        : fallback?.TypeTextAsync(r, ct) ?? Unavailable();
    public Task<UiInputResult> PressButtonAsync(UiButtonRequest r, CancellationToken ct = default) => IsElectron(r.DeviceIdentifier)
        ? Send(r.SessionId, r.DeviceIdentifier, new() { ["kind"] = "button", ["key"] = r.Button }, ct)
        : fallback?.PressButtonAsync(r, ct) ?? Unavailable();
}
