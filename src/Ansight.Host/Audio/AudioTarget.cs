namespace Ansight.Host.Audio;

internal sealed record AudioTarget(string SessionId, string AppId, string DeviceId, string Platform);
