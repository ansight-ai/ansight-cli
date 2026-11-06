using Ansight.Host;
using Ansight.Host.SimulatorAgent;
using System.Globalization;
using System.Text;

namespace Ansight.Cli.Commands.Tasks;

internal sealed record WorkspaceTestExtractionOutput(
    string Schema,
    string SessionId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string FilePath,
    WorkspaceTestExtraction Extraction);
