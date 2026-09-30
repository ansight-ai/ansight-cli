namespace Ansight.SimCtl;

public interface ISimCtlProcessLauncher
{
    ISimCtlProcess Start(SimCtlProcessStartRequest request);
}
