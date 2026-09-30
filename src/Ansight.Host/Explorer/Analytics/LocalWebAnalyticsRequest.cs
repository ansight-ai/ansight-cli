using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Host.Explorer.Analytics;

internal sealed record LocalWebAnalyticsRequest(
    string? Kind,
    bool? IsLive = null,
    string? Panel = null,
    string? Feature = null);
