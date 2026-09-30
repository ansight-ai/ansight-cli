namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed class SessionAnalysesBlobDocument
{
    public const string SchemaName = "ansight.session-analyses.v2";

    public string Schema { get; init; } = SchemaName;
    public required DateTimeOffset SavedAtUtc { get; init; }
    public required IReadOnlyList<SessionAnalysisRecord> Analyses { get; init; }
}
