using System.Buffers;
using System.Diagnostics;

namespace HelloLive.Desktop.Recording;

/// <summary>
/// Writes a live HTTP-FLV stream as a standalone FLV file.
/// Kuaishou live streams can start with timestamps that reflect how long the broadcaster
/// has already been live. Copying those bytes verbatim makes a local clip recorded for
/// a few minutes appear to be hours long. This writer rebases all FLV tag timestamps to
/// the first audio/video tag and only commits complete tags to disk.
/// </summary>
internal static class FlvStreamWriter
{
    private const int FlvHeaderSize = 9;
    private const int TagHeaderSize = 11;
    private const int PreviousTagSizeLength = 4;
    private const int MaximumExtraHeaderBytes = 1024 * 1024;

    public static async Task CopyNormalizedAsync(
        Stream input,
        Stream output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var header = new byte[FlvHeaderSize];
        await ReadExactlyAsync(input, header, cancellationToken);

        if (header[0] != (byte)'F'
            || header[1] != (byte)'L'
            || header[2] != (byte)'V')
        {
            throw new InvalidDataException("直播响应不是有效的 FLV 数据。");
        }

        var dataOffset = ReadUInt32BigEndian(header.AsSpan(5, 4));
        if (dataOffset < FlvHeaderSize
            || dataOffset > FlvHeaderSize + MaximumExtraHeaderBytes)
        {
            throw new InvalidDataException($"FLV Header DataOffset 异常：{dataOffset}");
        }

        await output.WriteAsync(header);

        if (dataOffset > FlvHeaderSize)
        {
            var extraHeaderLength = checked((int)dataOffset - FlvHeaderSize);
            var extraHeader = ArrayPool<byte>.Shared.Rent(extraHeaderLength);
            try
            {
                await ReadExactlyAsync(
                    input,
                    extraHeader.AsMemory(0, extraHeaderLength),
                    cancellationToken);
                await output.WriteAsync(
                    extraHeader.AsMemory(0, extraHeaderLength));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(extraHeader);
            }
        }

        var previousTagSize = new byte[PreviousTagSizeLength];
        await ReadExactlyAsync(input, previousTagSize, cancellationToken);
        await output.WriteAsync(previousTagSize);

        uint? baseTimestamp = null;
        uint lastNormalizedTimestamp = 0;
        var tagHeader = new byte[TagHeaderSize];
        var lastFlush = Stopwatch.StartNew();

        while (await TryReadExactlyAsync(input, tagHeader, cancellationToken))
        {
            var dataSize = ReadUInt24BigEndian(tagHeader.AsSpan(1, 3));
            uint originalTimestamp =
                ((uint)tagHeader[7] << 24)
                | (uint)ReadUInt24BigEndian(tagHeader.AsSpan(4, 3));

            var tagType = tagHeader[0] & 0x1F;
            var isMediaTag = tagType is 8 or 9;

            byte[]? rented = null;
            try
            {
                if (dataSize > 0)
                {
                    rented = ArrayPool<byte>.Shared.Rent(dataSize);
                    await ReadExactlyAsync(
                        input,
                        rented.AsMemory(0, dataSize),
                        cancellationToken);

                    if (tagType == 18)
                    {
                        // A live onMetaData block may carry duration/filesize values
                        // belonging to the upstream session. Zero them so players derive
                        // the local clip duration from our rebased media timestamps.
                        PatchAmfNumberPropertyToZero(
                            rented.AsSpan(0, dataSize),
                            "duration");
                        PatchAmfNumberPropertyToZero(
                            rented.AsSpan(0, dataSize),
                            "filesize");
                    }
                }

                // Do not use codec configuration tags as the timestamp origin.
                // Kuaishou/Douyin FLV commonly starts with AVC/AAC sequence headers at
                // timestamp 0, while the first real frame still carries the broadcaster's
                // long-running upstream timestamp (for example 55,877,535 ms). If the
                // sequence header becomes the base, the local clip appears many hours
                // long even though it only contains a few minutes of media.
                var payload = rented is null
                    ? ReadOnlySpan<byte>.Empty
                    : rented.AsSpan(0, dataSize);
                var isTimestampAnchor =
                    isMediaTag && IsActualMediaPayload(tagType, payload);

                if (isTimestampAnchor && baseTimestamp is null)
                    baseTimestamp = originalTimestamp;

                uint normalizedTimestamp = baseTimestamp is { } start
                    ? NormalizeTimestamp(originalTimestamp, start)
                    : 0u;

                // FLV DTS should normally be monotonic. If the upstream stream has a tiny
                // backward jump, do not let a player interpret it as another huge duration.
                if (isTimestampAnchor
                    && normalizedTimestamp + 5000 < lastNormalizedTimestamp)
                {
                    normalizedTimestamp = lastNormalizedTimestamp;
                }

                if (isTimestampAnchor)
                {
                    lastNormalizedTimestamp = Math.Max(
                        lastNormalizedTimestamp,
                        normalizedTimestamp);
                }

                WriteTimestamp(tagHeader, normalizedTimestamp);

                await ReadExactlyAsync(
                    input,
                    previousTagSize,
                    cancellationToken);

                // Once a complete FLV tag has been buffered, finish writing that whole tag
                // even when a normal stop is requested. This keeps a clean tag boundary.
                await output.WriteAsync(tagHeader);
                if (dataSize > 0 && rented is not null)
                {
                    await output.WriteAsync(
                        rented.AsMemory(0, dataSize));
                }

                await output.WriteAsync(previousTagSize);

                if (lastFlush.Elapsed >= TimeSpan.FromSeconds(2))
                {
                    await output.FlushAsync();
                    lastFlush.Restart();
                }
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }

        await output.FlushAsync();
    }

    private static bool IsActualMediaPayload(
        int tagType,
        ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
            return false;

        if (tagType == 8)
        {
            // SoundFormat 10 = AAC. AACPacketType 0 is AudioSpecificConfig
            // (sequence header); 1 is raw AAC media.
            var soundFormat = (payload[0] >> 4) & 0x0F;
            if (soundFormat == 10)
                return payload.Length >= 2 && payload[1] == 1;

            return true;
        }

        if (tagType == 9)
        {
            var first = payload[0];

            // Enhanced RTMP/FLV video header. PacketType 0 is SequenceStart;
            // coded-frame packet types are 1 and 3.
            if ((first & 0x80) != 0)
            {
                var packetType = first & 0x0F;
                return packetType is 1 or 3;
            }

            var codecId = first & 0x0F;

            // AVC (7) and commonly used legacy HEVC (12) both carry a packet-type
            // byte: 0 = sequence header, 1 = coded media, 2 = end of sequence.
            if (codecId is 7 or 12)
                return payload.Length >= 2 && payload[1] == 1;

            return true;
        }

        return false;
    }

    private static uint NormalizeTimestamp(uint timestamp, uint start)
    {
        if (timestamp >= start)
            return timestamp - start;

        var backwards = start - timestamp;

        // A small backwards value means the upstream stream reset/jittered.
        // A very large backwards value can be the 32-bit millisecond timestamp wrapping.
        if (backwards < 0x80000000u)
            return 0;

        return unchecked(timestamp + (uint.MaxValue - start) + 1u);
    }

    private static void WriteTimestamp(Span<byte> tagHeader, uint timestamp)
    {
        tagHeader[4] = (byte)(timestamp >> 16);
        tagHeader[5] = (byte)(timestamp >> 8);
        tagHeader[6] = (byte)timestamp;
        tagHeader[7] = (byte)(timestamp >> 24);
    }

    private static void PatchAmfNumberPropertyToZero(
        Span<byte> payload,
        string propertyName)
    {
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(propertyName);
        if (nameBytes.Length > ushort.MaxValue)
            return;

        for (var i = 0; i + 2 + nameBytes.Length + 1 + 8 <= payload.Length; i++)
        {
            if (payload[i] != (byte)(nameBytes.Length >> 8)
                || payload[i + 1] != (byte)nameBytes.Length)
            {
                continue;
            }

            if (!payload.Slice(i + 2, nameBytes.Length).SequenceEqual(nameBytes))
                continue;

            var typeOffset = i + 2 + nameBytes.Length;
            if (payload[typeOffset] != 0x00) // AMF0 Number
                continue;

            payload.Slice(typeOffset + 1, 8).Clear();
        }
    }

    private static async Task<bool> TryReadExactlyAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await input.ReadAsync(
                buffer[offset..],
                cancellationToken);

            if (read == 0)
            {
                if (offset == 0)
                    return false;

                throw new EndOfStreamException(
                    "HTTP-FLV 在一个 Tag 尚未完整接收时结束。");
            }

            offset += read;
        }

        return true;
    }

    private static async Task ReadExactlyAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (!await TryReadExactlyAsync(input, buffer, cancellationToken))
            throw new EndOfStreamException("HTTP-FLV 数据意外结束。");
    }

    private static int ReadUInt24BigEndian(ReadOnlySpan<byte> bytes)
        => (bytes[0] << 16) | (bytes[1] << 8) | bytes[2];

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> bytes)
        => ((uint)bytes[0] << 24)
           | ((uint)bytes[1] << 16)
           | ((uint)bytes[2] << 8)
           | bytes[3];
}
