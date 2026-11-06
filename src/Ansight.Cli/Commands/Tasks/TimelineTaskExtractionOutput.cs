using Ansight.Host;
using Ansight.Host.SimulatorAgent;
using System.Globalization;
using System.Text;

namespace Ansight.Cli.Commands.Tasks;

internal sealed record TimelineTaskExtractionOutput(
    string Schema,
    string SessionId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string FilePath,
    TimelineTaskExtraction Extraction);
