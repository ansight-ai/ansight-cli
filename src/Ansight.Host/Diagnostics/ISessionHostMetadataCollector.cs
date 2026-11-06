using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Ansight.Adb;
using Ansight.SimCtl;

namespace Ansight.Host.Diagnostics;

internal interface ISessionHostMetadataCollector
{
    Task<SessionHostMetadata> CollectAsync(SessionHostMetadata snapshot);
}
