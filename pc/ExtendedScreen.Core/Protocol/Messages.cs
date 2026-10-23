using System.Buffers.Binary;
using System.Text;

namespace ExtendedScreen.Core.Protocol;

public static class ProtocolConstants
{
    public const int Port = 27183;
    public const ushort Version = 1;
    public const int HeaderSize = 5;
    public const int MaxPayload = 16 * 1024 * 1024;
}

public enum MessageType : byte
{
    Hello = 1,
    Config = 2,
    VideoCsd = 3,
    VideoFrame = 4,
    RequestKeyframe = 5,
    Pointer = 6,
    Ping = 7,
    Pong = 8,
}

public enum PointerTool : byte
{
    Finger = 0,
    Pen = 1,
    Eraser = 2,
}

public enum PointerAction : byte
{
    Down = 0,
    Move = 1,
    Up = 2,
    Hover = 3,
    HoverExit = 4,
    Cancel = 5,
}

public sealed record HelloMessage(ushort Version, int Width, int Height, int DensityDpi, bool LowLatencyDecoder, string DeviceName)
{
    public byte[] Serialize()
    {
        var name = Encoding.UTF8.GetBytes(DeviceName);
        var buf = new byte[2 + 4 + 4 + 4 + 1 + 2 + name.Length];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s, Version);
        BinaryPrimitives.WriteInt32LittleEndian(s[2..], Width);
        BinaryPrimitives.WriteInt32LittleEndian(s[6..], Height);
        BinaryPrimitives.WriteInt32LittleEndian(s[10..], DensityDpi);
        s[14] = (byte)(LowLatencyDecoder ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(s[15..], (ushort)name.Length);
        name.CopyTo(s[17..]);
        return buf;
    }

    public static HelloMessage Parse(ReadOnlySpan<byte> p)
    {
        var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(p[15..]);
        return new HelloMessage(
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[2..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[6..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[10..]),
            (p[14] & 1) != 0,
            Encoding.UTF8.GetString(p.Slice(17, nameLen)));
    }
}

public sealed record ConfigMessage(int Width, int Height, int Fps, int Bitrate)
{
    public byte[] Serialize()
    {
        var buf = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(buf, Width);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), Height);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), Fps);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12), Bitrate);
        return buf;
    }

    public static ConfigMessage Parse(ReadOnlySpan<byte> p) => new(
        BinaryPrimitives.ReadInt32LittleEndian(p),
        BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
        BinaryPrimitives.ReadInt32LittleEndian(p[8..]),
        BinaryPrimitives.ReadInt32LittleEndian(p[12..]));
}

public static class VideoFrameMessage
{
    public const int HeaderSize = 9;

    /// <summary>Writes the 9-byte VIDEO_FRAME header (pts + flags) into <paramref name="dest"/>.</summary>
    public static void WriteHeader(Span<byte> dest, long ptsUs, bool keyframe)
    {
        BinaryPrimitives.WriteInt64LittleEndian(dest, ptsUs);
        dest[8] = (byte)(keyframe ? 1 : 0);
    }

    public static (long PtsUs, bool Keyframe) ReadHeader(ReadOnlySpan<byte> p) =>
        (BinaryPrimitives.ReadInt64LittleEndian(p), (p[8] & 1) != 0);
}

public readonly record struct PointerMessage(
    PointerTool Tool, PointerAction Action, uint PointerId,
    float X, float Y, float Pressure, float TiltX, float TiltY, byte Buttons)
{
    public const int Size = 1 + 1 + 4 + 4 * 5 + 1;

    public bool BarrelButton => (Buttons & 1) != 0;

    public byte[] Serialize()
    {
        var buf = new byte[Size];
        var s = buf.AsSpan();
        s[0] = (byte)Tool;
        s[1] = (byte)Action;
        BinaryPrimitives.WriteUInt32LittleEndian(s[2..], PointerId);
        BinaryPrimitives.WriteSingleLittleEndian(s[6..], X);
        BinaryPrimitives.WriteSingleLittleEndian(s[10..], Y);
        BinaryPrimitives.WriteSingleLittleEndian(s[14..], Pressure);
        BinaryPrimitives.WriteSingleLittleEndian(s[18..], TiltX);
        BinaryPrimitives.WriteSingleLittleEndian(s[22..], TiltY);
        s[26] = Buttons;
        return buf;
    }

    public static PointerMessage Parse(ReadOnlySpan<byte> p) => new(
        (PointerTool)p[0],
        (PointerAction)p[1],
        BinaryPrimitives.ReadUInt32LittleEndian(p[2..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[6..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[10..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[14..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[18..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[22..]),
        p[26]);
}

public static class PingMessage
{
    public static byte[] Serialize(long timestamp)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buf, timestamp);
        return buf;
    }

    public static long Parse(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadInt64LittleEndian(p);
}
