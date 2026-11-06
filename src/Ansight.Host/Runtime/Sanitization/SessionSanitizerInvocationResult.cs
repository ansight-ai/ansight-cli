using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Automation;

namespace Ansight.Host.Runtime.Sanitization;

internal sealed record SessionSanitizerInvocationResult(
    JsonObject? Value,
    bool WasRemoved,
    int RedactionCount);
