using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal readonly record struct ResolvedSessionLifecycleTransition(
    DateTimeOffset TimestampUtc,
    AppLifecycleState State);
