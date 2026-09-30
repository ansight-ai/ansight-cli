using Ansight.RemoteSimulator.Core.Companion;
using Ansight.RemoteSimulator.Core.WebRtc;

namespace Ansight.RemoteSimulator.Core.Server.WebRtc;

internal sealed record SessionRegistration(
    ISimulatorWebRtcSession Session,
    EventHandler<WebRtcInputMessageEventArgs> InputHandler,
    InputDispatcher InputDispatcher,
    IReadOnlySet<string> GrantedScopes,
    RemoteCompanionDevice? CompanionDevice = null,
    DateTimeOffset? ConnectedAtUtc = null);
