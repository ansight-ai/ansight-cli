namespace Ansight.Host.Runtime.WebSocketSessions;

using Ansight.Pairing;
using Ansight.Pairing.Models;
using Ansight.Tools;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.Runtime.Operations;
using Ansight.Infrastructure.Preferences;
using AppLifecycleState = global::Ansight.AppLifecycleState;
using HostOperationResult = Ansight.Host.Runtime.Contracts.OperationResult;

internal sealed record AuthorizedToolDescriptor(
    string ToolId,
    string Policy,
    bool IsExecutable,
    string? DenialCode,
    string? DenialReason);
