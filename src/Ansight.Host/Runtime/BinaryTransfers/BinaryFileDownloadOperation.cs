using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Pairing;
using Ansight.Tools;

namespace Ansight.Host.Runtime.BinaryTransfers;

public sealed class BinaryFileDownloadOperation
{
    private readonly TaskCompletionSource<BinaryFileDownloadResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string destinationDirectoryPath;

    internal BinaryFileDownloadOperation(
        string requestId,
        string sessionId,
        string sandboxPath,
        string destinationDirectoryPath,
        string? sandboxRoot,
        string downloadId,
        int chunkBytes)
    {
        RequestId = requestId;
        SessionId = sessionId;
        SandboxPath = sandboxPath;
        this.destinationDirectoryPath = destinationDirectoryPath;
        SandboxRoot = sandboxRoot;
        DownloadId = downloadId;
        ChunkBytes = chunkBytes;
        RequestEnvelope = CreateRequestEnvelope();
    }

    public string RequestId { get; }

    public string SessionId { get; }

    public string SandboxPath { get; }

    public string? SandboxRoot { get; }

    public string DownloadId { get; }

    public int ChunkBytes { get; }

    public ToolProtocolEnvelope RequestEnvelope { get; }

    public Task<BinaryFileDownloadResult> Completion => completion.Task;

    internal string DestinationDirectoryPath => destinationDirectoryPath;

    internal bool TrySetResult(BinaryFileDownloadResult result)
        => completion.TrySetResult(result);

    internal bool TrySetException(Exception exception)
        => completion.TrySetException(exception);

    public string SerializeRequestEnvelope(bool indented = false)
        => JsonSerializer.Serialize(RequestEnvelope, indented ? PairingJson.Pretty : PairingJson.Compact);

    private ToolProtocolEnvelope CreateRequestEnvelope()
    {
        var arguments = new JsonObject
        {
            ["path"] = SandboxPath,
            ["chunkBytes"] = ChunkBytes,
            ["downloadId"] = DownloadId
        };

        if (!string.IsNullOrWhiteSpace(SandboxRoot))
        {
            arguments["root"] = SandboxRoot;
        }

        return new ToolProtocolEnvelope
        {
            Type = ToolProtocolMessageTypes.CallType,
            Id = RequestId,
            SessionId = SessionId,
            Capability = ToolProtocolMessageTypes.Capability,
            Payload = new JsonObject
            {
                ["toolId"] = BinaryFileDownloadManager.BeginBinaryDownloadToolId,
                ["arguments"] = arguments
            }
        };
    }
}
