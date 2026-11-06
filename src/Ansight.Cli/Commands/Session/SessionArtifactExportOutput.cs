using System.Globalization;
using System.Text.Json;
using Ansight.Host;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionArtifactExportOutput(
    string Schema,
    string SessionId,
    SessionArtifactExportResult Result);
