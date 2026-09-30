namespace Ansight.RemoteSimulator.Core.Runtime;

public interface IRemoteRuntimeSource
{
    RemoteRuntimeSnapshot Current { get; }
}
