using Ansight.RemoteSimulator.Core.Access;
using Ansight.RemoteSimulator.Core.Server;
namespace Ansight.Cli.LocalSimulator;
internal interface IHostedSimulatorAccess : IAsyncDisposable
{
    IRemoteAccessAuthorizer Authorizer { get; }
    Task<CompanionAccessStatus> StartAsync(RemoteControlServer server, CancellationToken cancellationToken);
}
