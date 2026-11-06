using System.Text.Json.Nodes;

namespace Ansight.RemoteSimulator.Core.Devices;

public sealed record RemoteDeviceTestingResult(
    bool IsSuccess,
    string Message,
    JsonObject? Details = null);
