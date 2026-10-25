using System.Buffers.Binary;

namespace Ansight.Host.Sessions.Video;

internal static partial class SessionVideoMp4TimingInspector
{
    internal sealed record VideoInfo(int Width, int Height, TimeSpan Duration);

    internal static VideoInfo ReadVideoInfo(string filePath)
    {
        var bytes = ReadMovieBox(filePath, out _);
        var movie = FindBox(ReadBoxes(bytes, 0, bytes.Length), "moov");
        foreach (var track in ReadBoxes(bytes, movie.PayloadOffset, movie.EndOffset).Where(box => box.Type == "trak"))
        {
            var children = ReadBoxes(bytes, track.PayloadOffset, track.EndOffset);
            var media = FindBox(children, "mdia");
            if (!IsVideoHandler(bytes, FindBox(ReadBoxes(bytes, media.PayloadOffset, media.EndOffset), "hdlr"))) continue;
            var header = FindBox(children, "tkhd");
            EnsurePayloadSize(header, 84);
            return new VideoInfo((int)(ReadUInt32(bytes, header.EndOffset - 8) >> 16),
                (int)(ReadUInt32(bytes, header.EndOffset - 4) >> 16), ReadDuration(filePath));
        }
        throw new InvalidDataException("No video track found.");
    }

    internal static TimeSpan ReadDuration(string filePath)
    {
        var bytes = ReadMovieBox(filePath, out _);
        var movie = FindBox(ReadBoxes(bytes, 0, bytes.Length), "moov");
        var header = FindBox(ReadBoxes(bytes, movie.PayloadOffset, movie.EndOffset), "mvhd");
        var scale = ReadMediaTimeScale(bytes, header);
        var version = bytes[header.PayloadOffset];
        var offset = header.PayloadOffset + (version == 1 ? 24 : 16);
        EnsureAvailable(header, offset, version == 1 ? 8 : 4);
        var duration = version == 1 ? ReadUInt64(bytes, offset) : ReadUInt32(bytes, offset);
        return TimeSpan.FromSeconds((double)duration / scale);
    }

    // Native screen recorders emit frames on display changes. Preserve the final idle period
    // by extending the last sample, without transcoding or moving the encoded media bytes.
    internal static void ExtendFinalFrame(string filePath, TimeSpan capturedDuration)
    {
        var bytes = ReadMovieBox(filePath, out var movieOffset);
        var movie = FindBox(ReadBoxes(bytes, 0, bytes.Length), "moov");
        var children = ReadBoxes(bytes, movie.PayloadOffset, movie.EndOffset);
        var tracks = children.Where(box => box.Type == "trak").ToArray();
        if (tracks.Length != 1) throw new InvalidDataException("Test recording must contain one silent video track.");
        var track = tracks[0];
        var trackChildren = ReadBoxes(bytes, track.PayloadOffset, track.EndOffset);
        var media = FindBox(trackChildren, "mdia");
        var mediaChildren = ReadBoxes(bytes, media.PayloadOffset, media.EndOffset);
        if (!IsVideoHandler(bytes, FindBox(mediaChildren, "hdlr")))
            throw new InvalidDataException("Test recording has no video track.");
        var mediaHeader = FindBox(mediaChildren, "mdhd");
        var scale = ReadMediaTimeScale(bytes, mediaHeader);
        var information = FindBox(mediaChildren, "minf");
        var table = FindBox(ReadBoxes(bytes, information.PayloadOffset, information.EndOffset), "stbl");
        var timing = FindBox(ReadBoxes(bytes, table.PayloadOffset, table.EndOffset), "stts");
        EnsurePayloadSize(timing, 16);
        var entries = checked((int)ReadUInt32(bytes, timing.PayloadOffset + 4));
        if (entries <= 0 || entries > 10_000_000) throw new InvalidDataException("Invalid MP4 sample timing count.");
        EnsureAvailable(timing, timing.PayloadOffset + 8, checked(entries * 8));
        long duration = 0;
        for (var index = 0; index < entries; index++)
        {
            var offset = timing.PayloadOffset + 8 + index * 8;
            duration = checked(duration + (long)ReadUInt32(bytes, offset) * ReadUInt32(bytes, offset + 4));
        }
        var requested = checked((long)Math.Ceiling(capturedDuration.TotalSeconds * scale));
        if (requested <= duration) return;
        var extra = requested - duration;
        var lastOffset = timing.PayloadOffset + 8 + (entries - 1) * 8;
        var count = ReadUInt32(bytes, lastOffset);
        if (count == 0) throw new InvalidDataException("Invalid final MP4 sample count.");
        var delta = checked(ReadUInt32(bytes, lastOffset + 4) + (uint)extra);
        var payload = bytes.AsSpan(timing.PayloadOffset, timing.EndOffset - timing.PayloadOffset).ToArray();
        if (count > 1)
        {
            Array.Resize(ref payload, payload.Length + 8);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)entries + 1);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8 + (entries - 1) * 8), count - 1);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(payload.Length - 8), 1);
        }
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(payload.Length - 4), delta);

        var movieHeader = FindBox(children, "mvhd");
        var movieScale = ReadMediaTimeScale(bytes, movieHeader);
        var extraMovieUnits = checked((long)Math.Ceiling((decimal)extra * movieScale / scale));
        AddDuration(bytes, mediaHeader, 16, 24, extra);
        AddDuration(bytes, movieHeader, 16, 24, extraMovieUnits);
        AddDuration(bytes, FindBox(trackChildren, "tkhd"), 20, 28, extraMovieUnits);
        if (TryFindBox(trackChildren, "edts", out var edits))
        {
            var edit = FindBox(ReadBoxes(bytes, edits.PayloadOffset, edits.EndOffset), "elst");
            EnsurePayloadSize(edit, 8);
            var editCount = checked((int)ReadUInt32(bytes, edit.PayloadOffset + 4));
            var entrySize = bytes[edit.PayloadOffset] == 1 ? 20 : 12;
            if (editCount < 1) throw new InvalidDataException("Invalid MP4 edit list.");
            var last = checked(edit.PayloadOffset + 8 + (editCount - 1) * entrySize);
            EnsureAvailable(edit, last, entrySize);
            if (ReadUInt32(bytes, last + entrySize - 4) != 0x00010000)
                throw new InvalidDataException("Unsupported MP4 playback rate.");
            AddUnsignedDuration(bytes, last, bytes[edit.PayloadOffset], extraMovieUnits);
        }
        var rewritten = RewriteMovieBox(bytes, movie, timing.PayloadOffset, payload);
        using var output = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        output.Position = output.Length;
        output.Write(rewritten);
        output.Flush(flushToDisk: true);
        // Leave a same-sized free box at the original location: chunk offsets stay valid,
        // including for fast-start files whose original index precedes the media data.
        output.Position = movieOffset;
        if (ReadUInt32(bytes, 0) == 0)
        {
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)bytes.Length);
            output.Write(size);
        }
        output.Position = movieOffset + 4;
        output.Write("free"u8);
        output.Flush(flushToDisk: true);
    }

    private static void AddDuration(byte[] bytes, Mp4Box box, int versionZeroOffset, int versionOneOffset, long amount)
    {
        var version = bytes[box.PayloadOffset];
        var offset = box.PayloadOffset + (version == 1 ? versionOneOffset : versionZeroOffset);
        EnsureAvailable(box, offset, version == 1 ? 8 : 4);
        AddUnsignedDuration(bytes, offset, version, amount);
    }

    private static void AddUnsignedDuration(byte[] bytes, int offset, byte version, long amount)
    {
        if (version == 1)
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset), checked(ReadUInt64(bytes, offset) + (ulong)amount));
        else if (version == 0)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), checked(ReadUInt32(bytes, offset) + (uint)amount));
        else throw new InvalidDataException("Unsupported MP4 timing version.");
    }

    private static byte[] RewriteMovieBox(byte[] bytes, Mp4Box box, int timingPayloadOffset, byte[] replacement)
    {
        using var output = new MemoryStream();
        output.Write(new byte[4]);
        output.Write(Encoding.ASCII.GetBytes(box.Type));
        if (box.PayloadOffset == timingPayloadOffset) output.Write(replacement);
        else if (box.Type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "edts")
            foreach (var child in ReadBoxes(bytes, box.PayloadOffset, box.EndOffset))
                output.Write(RewriteMovieBox(bytes, child, timingPayloadOffset, replacement));
        else output.Write(bytes.AsSpan(box.PayloadOffset, box.EndOffset - box.PayloadOffset));
        var result = output.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(result, checked((uint)result.Length));
        return result;
    }
}
