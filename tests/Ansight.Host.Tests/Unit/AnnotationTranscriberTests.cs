using System.Buffers.Binary;
using Ansight.Host.Transcription;

namespace Ansight.Host.Tests.Unit;

public sealed class AnnotationTranscriberTests
{
    private static byte[] Wav(int samples = 3)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);

        for (var i = 0; i < samples; i++)
        {
            var value = i switch
            {
                0 => short.MinValue,
                1 => (short)0,
                _ => short.MaxValue
            };
            writer.Write(value);
        }

        return stream.ToArray();
    }

    [Fact]
    public void DecodesSignedPcm()
    {
        Assert.Equal(new float[] { -1, 0, 32767f / 32768 }, AnnotationTranscriber.ReadAudio(Wav(), "audio/wav"));
    }

    [Theory]
    [InlineData(22, 2)]
    [InlineData(24, 48000)]
    [InlineData(34, 32)]
    [InlineData(40, 100)]
    public void RejectsWrongFormatOrLength(int offset, int value)
    {
        var audio = Wav();
        BinaryPrimitives.WriteInt32LittleEndian(audio.AsSpan(offset), value);
        Assert.Throws<InvalidDataException>(() => AnnotationTranscriber.ReadAudio(audio, "audio/wav"));
    }

    [Fact]
    public void RejectsEmptyAndOverlongAudio()
    {
        Assert.Throws<InvalidDataException>(() => AnnotationTranscriber.ReadAudio(Wav(0), "audio/wav"));
        Assert.Throws<InvalidDataException>(() => AnnotationTranscriber.ReadAudio(Wav(16000 * 120 + 1), "audio/wav"));
        Assert.Throws<InvalidDataException>(() => AnnotationTranscriber.ReadAudio(Wav(), "audio/webm"));
    }

    [Fact]
    public async Task MissingModelDoesNotFallBackToCloud()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => AnnotationTranscriber.TranscribeAsync(Wav(), "audio/wav", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), default));
    }
}
