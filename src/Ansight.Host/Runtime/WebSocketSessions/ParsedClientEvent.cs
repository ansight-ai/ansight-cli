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

internal sealed record ParsedClientEvent(
    string? Type,
    string? Data,
    IReadOnlyList<SessionMetricChannel> MetricChannels,
    IReadOnlyList<SessionMetricSample> Metrics,
    IReadOnlyList<SessionApplicationEvent> Events,
    IReadOnlyList<SessionTouchInputRecord> Touches,
    SessionNetworkRequest? NetworkRequest,
    AppLifecycleState? AppState,
    DateTimeOffset? AppStateChangedUtc,
    DeviceAppProfile? DeviceProfile,
    string? DeviceProfileJson,
    ParsedSessionImageFrame? ImageFrame,
    SessionVisualTreeSnapshot? VisualTreeSnapshot);
