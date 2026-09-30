namespace Ansight.Host.Discovery;

internal interface IUdpPairingServer
{
    Task RunAsync(CancellationToken cancellationToken);
}
