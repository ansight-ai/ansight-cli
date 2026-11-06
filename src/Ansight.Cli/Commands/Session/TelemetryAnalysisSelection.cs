using Ansight.Cli.Commands.Cloud;
using Ansight.Host;
using Ansight.Infrastructure.Preferences;
using System.Globalization;
using System.Text;

namespace Ansight.Cli.Commands.Session;

internal enum TelemetryAnalysisSelection
{
    All,
    FpsDrop,
    MemorySpike
}
