using ExtendedScreen.Core.Video;
using ExtendedScreen.Server.Platform;

namespace ExtendedScreen.Server.Streaming;

/// <summary>
/// Sits between an encoder and a sink: sends SPS/PPS whenever they change and drops frames until the first
/// keyframe that carries them (decoders cannot start mid-GOP). Create one per encoder instance.
/// </summary>
public sealed class CsdTracker(IVideoSink sink)
{
    private byte[]? _lastCsd;

    public void Output(EncodedFrame frame)
    {
        if (frame.Keyframe && NalUtil.ExtractParameterSets(frame.Data) is { } csd
            && (_lastCsd is null || !csd.AsSpan().SequenceEqual(_lastCsd)))
        {
            _lastCsd = csd;
            sink.OnCodecConfig(csd);
        }
        if (_lastCsd is null) return;
        sink.OnFrame(frame);
    }
}
