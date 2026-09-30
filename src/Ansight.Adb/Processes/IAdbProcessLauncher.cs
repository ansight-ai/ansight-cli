namespace Ansight.Adb;

public interface IAdbProcessLauncher
{
    IAdbProcess Start(AdbProcessStartRequest request);
}
