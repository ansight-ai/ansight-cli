namespace Ansight.Host.Audio;

internal sealed record AudioFixtureInfo(string Sha256, double DurationMs, int SampleRate, int Channels, int BitsPerSample, long FrameCount);
