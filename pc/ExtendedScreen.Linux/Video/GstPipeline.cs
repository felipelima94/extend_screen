using System.Diagnostics;
using System.Runtime.InteropServices;
using ExtendedScreen.Core.Video;
using ExtendedScreen.Server.Streaming;
using static ExtendedScreen.Linux.Video.GstNative;

namespace ExtendedScreen.Linux.Video;

/// <summary>
/// PipeWire node → H.264 Annex-B access units, through a GStreamer pipeline ending in an appsink.
/// Picks the VA-API hardware encoder when present, then x264, then OpenH264.
/// </summary>
public sealed class GstPipeline : IDisposable
{
    /// <summary>Mutter only sends frames on damage; resend the last one this often so keyframe requests are honored.</summary>
    private const int KeepaliveMs = 250;

    private IntPtr _pipeline;
    private IntPtr _sink;
    private IntPtr _bus;

    public string EncoderName { get; }

    /// <param name="size">Size to negotiate with the PipeWire stream (virtual monitors adopt it); null takes the source size.</param>
    public GstPipeline(uint nodeId, (int Width, int Height)? size, int fps, int bitrate, bool allowHardware)
    {
        EnsureInitialized();
        var (name, encoder, format) = PickEncoder(fps, bitrate, allowHardware);
        EncoderName = name;

        var sizeCaps = size is { } s ? $",width={s.Width & ~1},height={s.Height & ~1}" : "";
        var description =
            $"pipewiresrc path={nodeId} always-copy=true keepalive-time={KeepaliveMs} " +
            $"! video/x-raw,max-framerate={fps}/1{sizeCaps} " +
            $"! videoconvert n-threads=4 ! video/x-raw,format={format} " +
            $"! {encoder} " +
            "! h264parse config-interval=-1 ! video/x-h264,stream-format=byte-stream,alignment=au " +
            "! appsink name=sink sync=false max-buffers=4 drop=false";

        _pipeline = gst_parse_launch(description, out var error);
        if (_pipeline == IntPtr.Zero) throw new InvalidOperationException($"GStreamer: {TakeError(error)}");
        if (error != IntPtr.Zero) TakeError(error); // recoverable warning
        _sink = gst_bin_get_by_name(_pipeline, "sink");
        _bus = gst_element_get_bus(_pipeline);
    }

    /// <summary>Encoders in order of preference: (name, element with properties, raw format it takes).</summary>
    private static (string Name, string Element, string Format) PickEncoder(int fps, int bitrate, bool allowHardware)
    {
        var gop = fps * 10; // keyframes are requested explicitly by the tablet; this is only a safety net
        var kbps = bitrate / 1000;
        if (allowHardware && HasElement("vah264enc"))
            return ("VA-API (GPU)", $"vah264enc name=enc rate-control=cbr bitrate={kbps} key-int-max={gop} b-frames=0 target-usage=7", "NV12");
        if (HasElement("x264enc"))
            return ("x264 (CPU)", $"x264enc name=enc tune=zerolatency speed-preset=ultrafast bitrate={kbps} key-int-max={gop} bframes=0 threads=0", "I420");
        if (HasElement("openh264enc"))
            return ("OpenH264 (CPU)", $"openh264enc name=enc usage-type=screen complexity=low rate-control=bitrate bitrate={bitrate} gop-size={gop} multi-thread=4", "I420");
        throw new InvalidOperationException(
            "Nenhum encoder H.264 do GStreamer encontrado. Instale gstreamer1-plugin-openh264 (ou x264 / mesa-va-drivers-freeworld do RPM Fusion).");
    }

    public static string? DescribeAvailableEncoder(bool allowHardware)
    {
        try { return PickEncoder(60, 20_000_000, allowHardware).Name; }
        catch { return null; }
    }

    /// <summary>Asks the encoder for an IDR frame (travels upstream from the appsink).</summary>
    public void RequestKeyframe()
    {
        var sink = _sink;
        if (sink == IntPtr.Zero) return;
        gst_element_send_event(sink, gst_video_event_new_upstream_force_key_unit(ClockTimeNone, true, 0));
    }

    /// <summary>Runs until cancelled or the stream fails. <paramref name="onConfig"/> is called with the size of the first frame.</summary>
    public void Run(IVideoSink sink, Action<int, int> onConfig, CancellationToken ct)
    {
        if (gst_element_set_state(_pipeline, StatePlaying) == StateChangeFailure)
            throw new InvalidOperationException("O pipeline do GStreamer não iniciou");

        var csd = new CsdTracker(sink);
        var clock = Stopwatch.StartNew();
        var configured = false;

        while (!ct.IsCancellationRequested)
        {
            ThrowOnBusError();
            var sample = gst_app_sink_try_pull_sample(_sink, 100_000_000); // 100 ms
            if (sample == IntPtr.Zero) continue;
            try
            {
                if (!configured)
                {
                    var (w, h) = ReadSize(sample);
                    onConfig(w, h);
                    sink.OnConfig(w, h);
                    configured = true;
                    RequestKeyframe();
                }

                var buffer = gst_sample_get_buffer(sample);
                if (buffer == IntPtr.Zero || !gst_buffer_map(buffer, out var map, MapRead)) continue;
                byte[] data;
                try
                {
                    data = new byte[(int)map.Size];
                    Marshal.Copy(map.Data, data, 0, data.Length);
                }
                finally
                {
                    gst_buffer_unmap(buffer, ref map);
                }
                csd.Output(new EncodedFrame(data, (long)clock.Elapsed.TotalMicroseconds, NalUtil.ContainsIdr(data)));
            }
            finally
            {
                gst_mini_object_unref(sample);
            }
        }
    }

    private static (int Width, int Height) ReadSize(IntPtr sample)
    {
        var structure = gst_caps_get_structure(gst_sample_get_caps(sample), 0);
        gst_structure_get_int(structure, "width", out var w);
        gst_structure_get_int(structure, "height", out var h);
        return (w, h);
    }

    private void ThrowOnBusError()
    {
        var msg = gst_bus_pop_filtered(_bus, MessageError | MessageEos);
        if (msg == IntPtr.Zero) return;
        try
        {
            // GstMessage.type sits right after the 64-byte GstMiniObject header.
            var type = Marshal.ReadInt32(msg, 64);
            if (type == MessageEos) throw new InvalidOperationException("O stream do PipeWire terminou");
            gst_message_parse_error(msg, out var error, out var debug);
            if (debug != IntPtr.Zero) g_free(debug);
            throw new InvalidOperationException($"GStreamer: {TakeError(error)}");
        }
        finally
        {
            gst_mini_object_unref(msg);
        }
    }

    public void Dispose()
    {
        if (_pipeline == IntPtr.Zero) return;
        gst_element_set_state(_pipeline, StateNull);
        if (_bus != IntPtr.Zero) gst_object_unref(_bus);
        if (_sink != IntPtr.Zero) gst_object_unref(_sink);
        gst_object_unref(_pipeline);
        _pipeline = _sink = _bus = IntPtr.Zero;
    }
}
