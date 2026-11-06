using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Devices;

internal sealed record IosPhysicalDeviceAppiumSession(
    string SessionId,
    int ViewportWidth,
    int ViewportHeight);
