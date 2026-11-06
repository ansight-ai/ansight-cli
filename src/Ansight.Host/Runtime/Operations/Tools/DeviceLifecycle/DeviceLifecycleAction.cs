using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DeviceLifecycle;

internal enum DeviceLifecycleAction
{
    StartDevice,
    LaunchApplication,
    ForegroundApplication,
    BackgroundApplication,
    TerminateApplication
}
