using System.Globalization;
using System.Text.Json;
using Ansight.Host;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionNormalizationOutput(
    string SchemaVersion,
    string SessionId,
    SessionNormalizationResult Result);
