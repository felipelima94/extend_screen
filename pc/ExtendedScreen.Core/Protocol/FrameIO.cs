using System.Buffers.Binary;

namespace ExtendedScreen.Core.Protocol;

public readonly record struct Frame(MessageType Type, byte[] Payload);

/// <summary>Reads length-prefixed frames from a stream. Not thread-safe; use one reader thread.</summary>
public sealed class FrameReader(Stream stream)
{
    private readonly byte[] _header = new byte[ProtocolConstants.HeaderSize];

    /// <returns>The next frame, or null when the stream ended cleanly.</returns>
    public async Task<Frame?> ReadAsync(CancellationToken ct = default)
    {
        if (!await ReadExactAsync(_header, ct)) return null;
        var type = (MessageType)_header[0];
        var length = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(1));
        if (length > ProtocolConstants.MaxPayload)
            throw new InvalidDataException($"Frame too large: {length} bytes");
        var payload = new byte[length];
        if (length > 0 && !await ReadExactAsync(payload, ct))
            throw new EndOfStreamException("Stream ended in the middle of a frame");
        return new Frame(type, payload);
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException();
            }
            read += n;
        }
        return true;
    }
}

/// <summary>Writes frames to a stream. Thread-safe: concurrent writers are serialized.</summary>
public sealed class FrameWriter(Stream stream)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public Task WriteAsync(MessageType type, byte[] payload, CancellationToken ct = default) =>
        WriteAsync(type, payload, ReadOnlyMemory<byte>.Empty, ct);

    /// <summary>Writes one frame whose payload is <paramref name="head"/> followed by <paramref name="tail"/>.</summary>
    public async Task WriteAsync(MessageType type, ReadOnlyMemory<byte> head, ReadOnlyMemory<byte> tail, CancellationToken ct = default)
    {
        var header = new byte[ProtocolConstants.HeaderSize];
        header[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), (uint)(head.Length + tail.Length));
        await _lock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(header, ct);
            if (!head.IsEmpty) await stream.WriteAsync(head, ct);
            if (!tail.IsEmpty) await stream.WriteAsync(tail, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }
}
