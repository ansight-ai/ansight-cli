using System.Text.Json;

namespace Ansight.RemoteSimulator.Core.AppInspection;

public sealed record RemoteAppInspectionRequest(
    string RequestId,
    string Operation,
    string SessionId,
    JsonElement Arguments);
