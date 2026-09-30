using Ansight.Host;

namespace Ansight.Cli.Commands.Host;

internal static class HostSessionProgressMessages
{
    public static string BuildPairingRequest(RuntimePairingEvent runtimeEvent)
        => $"[pairing] Connection request from '{runtimeEvent.ClientName}' at {runtimeEvent.RemoteAddress} for '{runtimeEvent.AppId}'.";

    public static string BuildPairingAccepted(RuntimePairingEvent runtimeEvent)
        => $"[pairing] Accepted '{runtimeEvent.ClientName}' for '{runtimeEvent.AppId}'.";

    public static string BuildSessionCreated(RuntimePairingEvent runtimeEvent)
        => $"[session] Created '{runtimeEvent.SessionId}'; waiting for the SDK live stream.";

    public static string BuildPortalSuggestion(string sessionId)
        => $"[next] Open the local portal in another terminal: ansight serve --session '{sessionId}' --open";

    public static string BuildPairingRejected(RuntimePairingEvent runtimeEvent)
    {
        var reason = runtimeEvent.ReasonMessage ?? runtimeEvent.ReasonCode ?? "Unknown reason.";
        return $"[pairing] Rejected '{runtimeEvent.ClientName}' for '{runtimeEvent.AppId}': {reason}";
    }

    public static string BuildCaptureStarted(RuntimeSessionCaptureEvent runtimeEvent)
        => $"[capture] Live stream connected for '{runtimeEvent.AppId}'; recording session '{runtimeEvent.SessionId}'.";

    public static string BuildCaptureStopped(RuntimeSessionCaptureEvent runtimeEvent)
    {
        var detail = string.IsNullOrWhiteSpace(runtimeEvent.Message)
            ? runtimeEvent.Status
            : runtimeEvent.Message;
        return $"[capture] Session '{runtimeEvent.SessionId}' stopped: {detail}";
    }
}
