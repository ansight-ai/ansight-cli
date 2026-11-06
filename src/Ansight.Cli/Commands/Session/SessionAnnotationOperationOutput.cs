using System.Globalization;
using System.Text.Json;
using Ansight.Host;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionAnnotationOperationOutput(
    string Schema,
    string SessionId,
    OperationResult Result,
    SessionAnnotation Annotation);
