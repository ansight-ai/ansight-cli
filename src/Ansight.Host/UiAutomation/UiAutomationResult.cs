using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;

namespace Ansight.Host.UiAutomation;

public sealed record UiAutomationResult(
    UiAutomationOperation Operation,
    bool IsSuccess,
    string Message,
    JsonObject Payload);
