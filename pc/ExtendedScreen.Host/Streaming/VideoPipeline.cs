using System.Diagnostics;
using ExtendedScreen.Core.Video;
using ExtendedScreen.Host.Capture;
using ExtendedScreen.Host.Encoding;

namespace ExtendedScreen.Host.Streaming;

public interface IVideoSink
{
    /// <summary>Called whenever the encoder is (re)built, before any frame of that size.</summary>
    void OnConfig(int width, int height);

    void OnCodecConfig(byte[] spsPps);

    void OnFrame(EncodedFrame frame);
}

/// <summary>Capture → NV12 → H.264 loop for one monitor. Rebuilds itself when the monitor changes resolution.</summary>
public sealed class VideoPipeline(string deviceName, int fps, int bitrate, bool drawCursor, bool allowHardware, Action<string> log)
{
    /// <summary>When the desktop is static, re-encode the last image this often so keyframe requests are honored.</summary>
    private const int IdleRepeatMs = 250;

    private MfH264Encoder? _encoder;

    public void RequestKeyframe() => _encoder?.RequestKeyframe();

    public void Run(IVideoSink sink, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                RunOnce(sink, ct);
            }
            catch (DisplayChangedException ex)
            {
                log($"Reiniciando captura: {ex.Message}");
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                log($"Erro no pipeline de vídeo: {ex.Message}");
                ct.WaitHandle.WaitOne(1000);
            }
        }
    }

    private void RunOnce(IVideoSink sink, CancellationToken ct)
    {
        using var capture = new DesktopCapture(deviceName) { DrawCursor = drawCursor };
        // Grab the first image before the converter binds to the texture.
        capture.Acquire(500);
        using var converter = new Nv12Converter(capture.Device, capture.Context, capture.Frame, capture.Width, capture.Height, capture.Rotation);
        using var encoder = MfH264Encoder.Create(capture.Device, converter.Width, converter.Height, fps, bitrate,
            converter.ReadBack, log, allowHardware);
        _encoder = encoder;
        log($"Capturando {deviceName} ({capture.Width}x{capture.Height}) em {capture.AdapterName}");
        sink.OnConfig(converter.Width, converter.Height);

        var clock = Stopwatch.StartNew();
        var frameInterval = TimeSpan.FromSeconds(1.0 / fps);
        var nextDue = TimeSpan.Zero;
        var first = true;
        byte[]? lastCsd = null;

        EncoderInput? Pull()
        {
            if (ct.IsCancellationRequested) return null;

            var wait = nextDue - clock.Elapsed;
            if (wait > TimeSpan.Zero) ct.WaitHandle.WaitOne(wait);
            if (ct.IsCancellationRequested) return null;

            if (!first) capture.Acquire(IdleRepeatMs);
            first = false;

            var now = clock.Elapsed;
            nextDue = now + frameInterval;
            return new EncoderInput(converter.Convert(), now.Ticks);
        }

        void Output(EncodedFrame frame)
        {
            if (frame.Keyframe && NalUtil.ExtractParameterSets(frame.Data) is { } csd
                && (lastCsd is null || !csd.AsSpan().SequenceEqual(lastCsd)))
            {
                lastCsd = csd;
                sink.OnCodecConfig(csd);
            }
            // Decoders cannot start mid-GOP.
            if (lastCsd is null) return;
            sink.OnFrame(frame);
        }

        try
        {
            encoder.Run(Pull, Output, ct);
        }
        finally
        {
            _encoder = null;
        }
    }
}
