using Ansight.Host;
using Ansight.Host.SimulatorAgent;
using System.Globalization;
using System.Text;

namespace Ansight.Cli.Commands.Tasks;

internal sealed record MaestroFlowExtractionOutput(
    string Schema,
    string SessionId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string FilePath,
    MaestroFlowExtraction Extraction,
    string? Model);
