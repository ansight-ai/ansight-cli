using System.Buffers.Binary;

namespace Ansight.Host.Sessions.Video;

internal static class SessionVideoMp4TimingInspector
{
    private const int MicrosecondsPerSecond = 1_000_000;

    public static IReadOnlyList<long> ReadPresentationTimesUs(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var bytes = File.ReadAllBytes(filePath);
        var rootBoxes = ReadBoxes(bytes, 0, bytes.Length);
        var movieBox = FindBox(rootBoxes, "moov");
        foreach (var trackBox in ReadBoxes(bytes, movieBox.PayloadOffset, movieBox.EndOffset)
                     .Where(static box => box.Type == "trak"))
        {
            var mediaBox = FindBox(ReadBoxes(bytes, trackBox.PayloadOffset, trackBox.EndOffset), "mdia");
            var mediaBoxes = ReadBoxes(bytes, mediaBox.PayloadOffset, mediaBox.EndOffset);
            if (!IsVideoHandler(bytes, FindBox(mediaBoxes, "hdlr")))
            {
                continue;
            }

            var timeScale = ReadMediaTimeScale(bytes, FindBox(mediaBoxes, "mdhd"));
            var mediaInformationBox = FindBox(mediaBoxes, "minf");
            var sampleTableBox = FindBox(
                ReadBoxes(bytes, mediaInformationBox.PayloadOffset, mediaInformationBox.EndOffset),
                "stbl");
            var sampleTableBoxes = ReadBoxes(bytes, sampleTableBox.PayloadOffset, sampleTableBox.EndOffset);
            var decodeTimes = ReadDecodeTimes(bytes, FindBox(sampleTableBoxes, "stts"));
            var compositionOffsets = TryFindBox(sampleTableBoxes, "ctts", out var compositionTimeBox)
                ? ReadCompositionOffsets(bytes, compositionTimeBox, decodeTimes.Count)
                : new long[decodeTimes.Count];
            var presentationTimes = decodeTimes
                .Select((decodeTime, index) => checked(decodeTime + compositionOffsets[index]))
                .Order()
                .ToArray();
            if (presentationTimes.Length == 0)
            {
                throw new InvalidDataException("The MP4 video track contains no samples.");
            }

            var firstPresentationTime = presentationTimes[0];
            return presentationTimes
                .Select(value => ConvertToMicroseconds(value - firstPresentationTime, timeScale))
                .ToArray();
        }

        throw new InvalidDataException("The encoded MP4 does not contain a video track.");
    }

    private static bool IsVideoHandler(byte[] bytes, Mp4Box handlerBox)
    {
        EnsurePayloadSize(handlerBox, 12);
        return ReadType(bytes, handlerBox.PayloadOffset + 8) == "vide";
    }

    private static uint ReadMediaTimeScale(byte[] bytes, Mp4Box mediaHeaderBox)
    {
        EnsurePayloadSize(mediaHeaderBox, 24);
        var version = bytes[mediaHeaderBox.PayloadOffset];
        var timeScaleOffset = version == 1
            ? mediaHeaderBox.PayloadOffset + 20
            : mediaHeaderBox.PayloadOffset + 12;
        if (timeScaleOffset + sizeof(uint) > mediaHeaderBox.EndOffset)
        {
            throw new InvalidDataException("The MP4 media header is truncated.");
        }

        var timeScale = ReadUInt32(bytes, timeScaleOffset);
        return timeScale == 0
            ? throw new InvalidDataException("The MP4 video track has an invalid time scale.")
            : timeScale;
    }

    private static IReadOnlyList<long> ReadDecodeTimes(byte[] bytes, Mp4Box timeToSampleBox)
    {
        EnsurePayloadSize(timeToSampleBox, 8);
        var entryCount = ReadUInt32(bytes, timeToSampleBox.PayloadOffset + 4);
        var offset = timeToSampleBox.PayloadOffset + 8;
        var decodeTimes = new List<long>();
        long decodeTime = 0;
        for (uint entryIndex = 0; entryIndex < entryCount; entryIndex++)
        {
            EnsureAvailable(timeToSampleBox, offset, 8);
            var sampleCount = ReadUInt32(bytes, offset);
            var sampleDelta = ReadUInt32(bytes, offset + 4);
            if (sampleCount > 10_000_000 || decodeTimes.Count + sampleCount > 10_000_000)
            {
                throw new InvalidDataException("The MP4 video track declares an unreasonable sample count.");
            }

            for (uint sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                decodeTimes.Add(decodeTime);
                decodeTime = checked(decodeTime + sampleDelta);
            }

            offset += 8;
        }

        return decodeTimes;
    }

    private static IReadOnlyList<long> ReadCompositionOffsets(
        byte[] bytes,
        Mp4Box compositionTimeBox,
        int expectedSampleCount)
    {
        EnsurePayloadSize(compositionTimeBox, 8);
        var version = bytes[compositionTimeBox.PayloadOffset];
        var entryCount = ReadUInt32(bytes, compositionTimeBox.PayloadOffset + 4);
        var offset = compositionTimeBox.PayloadOffset + 8;
        var compositionOffsets = new List<long>(expectedSampleCount);
        for (uint entryIndex = 0; entryIndex < entryCount; entryIndex++)
        {
            EnsureAvailable(compositionTimeBox, offset, 8);
            var sampleCount = ReadUInt32(bytes, offset);
            var rawCompositionOffset = ReadUInt32(bytes, offset + 4);
            long compositionOffset = version == 1
                ? unchecked((int)rawCompositionOffset)
                : rawCompositionOffset;
            if (sampleCount > 10_000_000 || compositionOffsets.Count + sampleCount > 10_000_000)
            {
                throw new InvalidDataException("The MP4 video track declares an unreasonable composition sample count.");
            }

            for (uint sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                compositionOffsets.Add(compositionOffset);
            }

            offset += 8;
        }

        if (compositionOffsets.Count != expectedSampleCount)
        {
            throw new InvalidDataException("The MP4 composition timing table does not match the video sample count.");
        }

        return compositionOffsets;
    }

    private static IReadOnlyList<Mp4Box> ReadBoxes(byte[] bytes, int startOffset, int endOffset)
    {
        var boxes = new List<Mp4Box>();
        var offset = startOffset;
        while (offset + 8 <= endOffset)
        {
            var size32 = ReadUInt32(bytes, offset);
            var type = ReadType(bytes, offset + 4);
            long size = size32;
            var headerSize = 8;
            if (size32 == 1)
            {
                if (offset + 16 > endOffset)
                {
                    throw new InvalidDataException("The MP4 contains a truncated extended box header.");
                }

                size = checked((long)ReadUInt64(bytes, offset + 8));
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                size = endOffset - offset;
            }

            if (size < headerSize || offset + size > endOffset)
            {
                throw new InvalidDataException($"The MP4 contains an invalid '{type}' box.");
            }

            var boxEndOffset = checked(offset + (int)size);
            boxes.Add(new Mp4Box(type, offset + headerSize, boxEndOffset));
            offset = boxEndOffset;
        }

        return boxes;
    }

    private static Mp4Box FindBox(IReadOnlyList<Mp4Box> boxes, string type)
        => TryFindBox(boxes, type, out var box)
            ? box
            : throw new InvalidDataException($"The MP4 is missing its '{type}' box.");

    private static bool TryFindBox(IReadOnlyList<Mp4Box> boxes, string type, out Mp4Box box)
    {
        foreach (var candidate in boxes)
        {
            if (candidate.Type == type)
            {
                box = candidate;
                return true;
            }
        }

        box = default;
        return false;
    }

    private static long ConvertToMicroseconds(long value, uint timeScale)
    {
        var scaled = (decimal)value * MicrosecondsPerSecond / timeScale;
        return checked((long)Math.Round(scaled, MidpointRounding.AwayFromZero));
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
        => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, sizeof(uint)));

    private static ulong ReadUInt64(byte[] bytes, int offset)
        => BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset, sizeof(ulong)));

    private static string ReadType(byte[] bytes, int offset)
        => string.Create(4, (bytes, offset), static (destination, state) =>
        {
            for (var index = 0; index < destination.Length; index++)
            {
                destination[index] = (char)state.bytes[state.offset + index];
            }
        });

    private static void EnsurePayloadSize(Mp4Box box, int minimumSize)
    {
        if (box.EndOffset - box.PayloadOffset < minimumSize)
        {
            throw new InvalidDataException($"The MP4 '{box.Type}' box is truncated.");
        }
    }

    private static void EnsureAvailable(Mp4Box box, int offset, int byteCount)
    {
        if (offset < box.PayloadOffset || offset + byteCount > box.EndOffset)
        {
            throw new InvalidDataException($"The MP4 '{box.Type}' timing table is truncated.");
        }
    }

    private readonly record struct Mp4Box(
        string Type,
        int PayloadOffset,
        int EndOffset);
}
