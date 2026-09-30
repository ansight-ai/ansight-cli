using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;
using Ansight.Host.Runtime;

namespace Ansight.Host.SimulatorAgent.Tools;

internal interface IToolGateway
{
    void BeginRun(
        string sessionId,
        SecretAccess secretAccess,
        string? targetDeviceIdentifier = null);

    void EndRun();

    void RebindRun(
        string sessionId,
        string? targetDeviceIdentifier = null);

    JsonArray BuildOpenAiToolDefinitions(
        IReadOnlyList<RepositoryTaskShortcut>? repositoryTasks = null,
        SessionCapabilities? capabilities = null,
        bool appGraphEnabled = false);

    Task<IReadOnlyList<RepositoryTaskShortcut>> GetRepositoryTaskShortcutsAsync(
        string sessionId,
        string instruction,
        CancellationToken cancellationToken,
        Action<SimulatorAgentRepositoryTaskDiscoveryTrace>? trace = null);

    OpenAiFunctionCall NormalizeFunctionCall(
        OpenAiFunctionCall call);

    bool IsReadOnlyAppToolCall(JsonObject arguments, string sessionId);

    Task<ToolSessionContext?> GetSessionContextAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<SessionCapabilities> GetSessionCapabilitiesAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<ToolCallResult?> CaptureInitialObservationAsync(
        string sessionId,
        SessionCapabilities capabilities,
        string correlationId,
        CancellationToken cancellationToken);

    Task<ToolSessionContext?> WaitForConnectedSessionAsync(
        string currentSessionId,
        string appId,
        string? targetDeviceIdentifier,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<ToolCallResult> ExecuteAsync(
        string toolName,
        JsonObject arguments,
        string sessionId,
        string correlationId,
        CancellationToken cancellationToken,
        OperationExecutionContext? context = null);
}
