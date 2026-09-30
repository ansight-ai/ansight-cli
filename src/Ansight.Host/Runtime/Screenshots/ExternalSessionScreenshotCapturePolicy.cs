namespace Ansight.Host.Runtime.Screenshots;

using System.Text.Json.Nodes;

internal sealed record ExternalSessionScreenshotCapturePolicy(
    string Mode,
    string Source,
    string Reason,
    int IntervalMilliseconds,
    int Quality,
    int? MaxWidth)
{
    public const string AppMode = "app";
    public const string HostMode = "host";
    public const string ControlVersionPropertyName = "sessionJpegCaptureControlVersion";
    public const int ControlVersion = 1;

    public static ExternalSessionScreenshotCapturePolicy App(string reason)
        => new(AppMode, "app", reason, 2_000, 60, ExternalSessionScreenshotCaptureRequest.Default.MaxWidth);

    public static ExternalSessionScreenshotCapturePolicy Host(string source, int? maxWidth, int intervalMilliseconds = 2_000)
        => new(HostMode, source, "Ansight verified external screenshot capture.", intervalMilliseconds, 60, maxWidth);

    public static bool SupportsHostControl(JsonObject? profilePayload)
        => profilePayload?[ControlVersionPropertyName] is JsonValue controlVersionNode
           && controlVersionNode.TryGetValue<int>(out var controlVersion)
           && controlVersion >= ControlVersion;

    public JsonObject ToResponsePayload()
        => new()
        {
            ["sessionJpegCapture"] = new JsonObject
            {
                ["mode"] = Mode,
                ["source"] = Source,
                ["fallbackMode"] = AppMode,
                ["reason"] = Reason,
                ["intervalMilliseconds"] = IntervalMilliseconds,
                ["quality"] = Quality,
                ["maxWidth"] = MaxWidth
            }
        };
}
