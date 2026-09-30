namespace Ansight.Host.Runtime.WebSocketSessions;

/// <summary>
/// Wire-level event type symbols accepted by the WebSocket session manager.
/// </summary>
internal static class WebSocketClientEventConstants
{
    /// <summary>
    /// Client text event that appends a log entry to the current host session.
    /// </summary>
    internal const string ClientLog = "CLIENT_LOG";

    /// <summary>
    /// Client text event that marks the current session stream as complete.
    /// </summary>
    internal const string ClientDone = "CLIENT_DONE";

    /// <summary>
    /// Client text event that registers the available telemetry metric channels.
    /// </summary>
    internal const string ClientMetricChannels = "CLIENT_METRIC_CHANNELS";

    /// <summary>
    /// Client text event that carries telemetry metric samples.
    /// </summary>
    internal const string ClientMetrics = "CLIENT_METRICS";

    /// <summary>
    /// Client text event that carries app-defined event records.
    /// </summary>
    internal const string ClientEvents = "CLIENT_EVENTS";

    /// <summary>
    /// Client text event that carries packed touch input records.
    /// </summary>
    internal const string ClientTouchInput = "CLIENT_TOUCH_INPUT";

    /// <summary>
    /// Client text event carrying one completed sanitized HTTP request.
    /// </summary>
    internal const string ClientNetworkRequest = "CLIENT_NETWORK_REQUEST";

    /// <summary>
    /// Client text event that reports the current app lifecycle state.
    /// </summary>
    internal const string ClientAppState = "CLIENT_APP_STATE";

    /// <summary>
    /// Client text event that carries device and app profile details.
    /// </summary>
    internal const string DeviceAppProfile = "DeviceAppProfile";

    /// <summary>
    /// Client text or binary event that carries a JPEG screenshot frame.
    /// </summary>
    internal const string ClientJpeg = "CLIENT_JPEG";

    /// <summary>
    /// Client text event that carries a visual tree captured alongside a screenshot frame.
    /// </summary>
    internal const string ClientVisualTree = "CLIENT_VISUAL_TREE";
}
