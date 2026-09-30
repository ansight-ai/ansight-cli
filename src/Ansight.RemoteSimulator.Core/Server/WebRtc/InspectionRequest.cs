using System.Text.Json;

namespace Ansight.RemoteSimulator.Core.Server.WebRtc;

internal sealed record InspectionRequest(
    string Kind,
    string RequestId,
    string Operation,
    string SessionId,
    JsonElement Arguments);
