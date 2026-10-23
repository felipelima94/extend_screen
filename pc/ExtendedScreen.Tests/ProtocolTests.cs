using ExtendedScreen.Core.Protocol;

namespace ExtendedScreen.Tests;

public class ProtocolTests
{
    [Fact]
    public async Task Frames_RoundTrip()
    {
        var ms = new MemoryStream();
        var writer = new FrameWriter(ms);
        await writer.WriteAsync(MessageType.Ping, PingMessage.Serialize(42));
        await writer.WriteAsync(MessageType.VideoFrame, new byte[] { 1, 2 }, new byte[] { 3, 4, 5 });
        await writer.WriteAsync(MessageType.RequestKeyframe, []);

        ms.Position = 0;
        var reader = new FrameReader(ms);

        var f1 = await reader.ReadAsync();
        Assert.Equal(MessageType.Ping, f1!.Value.Type);
        Assert.Equal(42, PingMessage.Parse(f1.Value.Payload));

        var f2 = await reader.ReadAsync();
        Assert.Equal(MessageType.VideoFrame, f2!.Value.Type);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, f2.Value.Payload);

        var f3 = await reader.ReadAsync();
        Assert.Equal(MessageType.RequestKeyframe, f3!.Value.Type);
        Assert.Empty(f3.Value.Payload);

        Assert.Null(await reader.ReadAsync());
    }

    [Fact]
    public async Task Truncated_Frame_Throws()
    {
        var ms = new MemoryStream([(byte)MessageType.Ping, 8, 0, 0, 0, 1, 2]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => new FrameReader(ms).ReadAsync());
    }

    [Fact]
    public void Hello_RoundTrip()
    {
        var hello = new HelloMessage(1, 2560, 1600, 320, true, "Galaxy Tab S9 ção");
        Assert.Equal(hello, HelloMessage.Parse(hello.Serialize()));
    }

    [Fact]
    public void Config_RoundTrip()
    {
        var config = new ConfigMessage(1920, 1200, 60, 12_000_000);
        Assert.Equal(config, ConfigMessage.Parse(config.Serialize()));
    }

    [Fact]
    public void Pointer_RoundTrip()
    {
        var p = new PointerMessage(PointerTool.Pen, PointerAction.Move, 3, 0.25f, 0.75f, 0.5f, -12.5f, 30f, 1);
        var bytes = p.Serialize();
        Assert.Equal(PointerMessage.Size, bytes.Length);
        var parsed = PointerMessage.Parse(bytes);
        Assert.Equal(p, parsed);
        Assert.True(parsed.BarrelButton);
    }

    [Fact]
    public void VideoFrameHeader_RoundTrip()
    {
        var buf = new byte[VideoFrameMessage.HeaderSize];
        VideoFrameMessage.WriteHeader(buf, 123456789L, true);
        Assert.Equal((123456789L, true), VideoFrameMessage.ReadHeader(buf));
    }
}
