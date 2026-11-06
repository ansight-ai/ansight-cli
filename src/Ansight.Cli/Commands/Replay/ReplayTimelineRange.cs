using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ansight.Host;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Replay;

internal sealed record ReplayTimelineRange(
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc,
    string? AnnotationId);
