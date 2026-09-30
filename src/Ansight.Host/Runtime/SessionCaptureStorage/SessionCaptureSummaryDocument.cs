namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed class SessionCaptureSummaryDocument
{
    public const string SchemaName = "ansight.session-capture-summary.v10";

    public string Schema { get; init; } = SchemaName;
    public required DateTimeOffset SavedAtUtc { get; init; }
    public required SessionCaptureSummary Session { get; init; }
}
