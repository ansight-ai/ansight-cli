using System.Globalization;
using System.Text.Json;
using Ansight.Host;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionExtractionOutput(
    string Schema,
    string SourceSessionId,
    string Source,
    SessionExtractionResult Result);
