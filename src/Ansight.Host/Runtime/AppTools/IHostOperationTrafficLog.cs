using Ansight.Tools;

namespace Ansight.Host.Runtime.AppTools;

internal interface IHostOperationTrafficLog : IDisposable
{
    bool IsCaptureEnabled { get; }

    string DirectoryPath { get; }

    string? CurrentFilePath { get; }

    void RecordToolBridgeRequest(
        string sessionId,
        string appId,
        string clientName,
        ToolProtocolEnvelope envelope,
        string requestBody,
        AppToolBridgeRequestContext? requestContext);

    void RecordToolBridgeResponse(
        string sessionId,
        string appId,
        string clientName,
        ToolProtocolEnvelope requestEnvelope,
        ToolProtocolEnvelope responseEnvelope,
        string requestBody,
        string responseBody,
        long durationMs,
        AppToolBridgeRequestContext? requestContext);

    void RecordToolBridgeFailure(
        string sessionId,
        string appId,
        string clientName,
        ToolProtocolEnvelope envelope,
        string requestBody,
        string message,
        AppToolBridgeRequestContext? requestContext,
        long durationMs);

    void RecordToolBridgeUnmatchedResponse(
        string sessionId,
        string appId,
        string clientName,
        ToolProtocolEnvelope envelope,
        string responseBody,
        string reason);
}
