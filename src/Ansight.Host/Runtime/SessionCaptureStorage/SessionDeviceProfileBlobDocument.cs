namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal sealed class SessionDeviceProfileBlobDocument
{
    public const string SchemaName = "ansight.session-device-profile.v2";

    public string Schema { get; init; } = SchemaName;
    public required DateTimeOffset SavedAtUtc { get; init; }
    public string? ProfileJson { get; init; }
}
