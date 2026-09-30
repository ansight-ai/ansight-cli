namespace Ansight.Host.Audio;

internal sealed record AudioFixture(string Path, AudioFixtureInfo Info, byte[] PcmBytes);
