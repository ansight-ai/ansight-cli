using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Pairing;

public enum RuntimePairingEventKind
{
    DiscoveryReceived,
    PairingAccepted,
    PairingRejected
}
