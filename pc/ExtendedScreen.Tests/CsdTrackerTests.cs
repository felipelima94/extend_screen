using ExtendedScreen.Server.Platform;
using ExtendedScreen.Server.Streaming;

namespace ExtendedScreen.Tests;

public class CsdTrackerTests
{
    private static readonly byte[] KeyA = [0, 0, 0, 1, 0x67, 0xAA, 0, 0, 0, 1, 0x68, 0xCC, 0, 0, 0, 1, 0x65, 0x01];
    private static readonly byte[] KeyB = [0, 0, 0, 1, 0x67, 0xBB, 0, 0, 0, 1, 0x68, 0xCC, 0, 0, 0, 1, 0x65, 0x01];
    private static readonly byte[] Delta = [0, 0, 0, 1, 0x41, 0x9A];

    private sealed class Recorder : IVideoSink
    {
        public readonly List<byte[]> Csd = [];
        public readonly List<EncodedFrame> Frames = [];

        public void OnConfig(int width, int height) { }
        public void OnCodecConfig(byte[] spsPps) => Csd.Add(spsPps);
        public void OnFrame(EncodedFrame frame) => Frames.Add(frame);
    }

    [Fact]
    public void DropsFramesUntilFirstParameterSets()
    {
        var sink = new Recorder();
        var tracker = new CsdTracker(sink);
        tracker.Output(new EncodedFrame(Delta, 0, false));
        tracker.Output(new EncodedFrame(KeyA, 1, true));
        tracker.Output(new EncodedFrame(Delta, 2, false));

        Assert.Single(sink.Csd);
        Assert.Equal([1L, 2L], sink.Frames.Select(f => f.PtsUs));
    }

    [Fact]
    public void ResendsParameterSetsOnlyWhenTheyChange()
    {
        var sink = new Recorder();
        var tracker = new CsdTracker(sink);
        tracker.Output(new EncodedFrame(KeyA, 0, true));
        tracker.Output(new EncodedFrame(KeyA, 1, true));
        tracker.Output(new EncodedFrame(KeyB, 2, true));

        Assert.Equal(2, sink.Csd.Count);
        Assert.Equal(3, sink.Frames.Count);
    }
}
