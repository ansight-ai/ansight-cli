using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Feedback;

internal sealed record AnnotatedFeedbackTransferCompletion(
    string SessionId,
    string TransferId,
    string ClientAnnotationId,
    DateTimeOffset CapturedAtUtc,
    string BundlePath);
