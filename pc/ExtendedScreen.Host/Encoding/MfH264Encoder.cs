using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace ExtendedScreen.Host.Encoding;

public readonly record struct EncoderInput(ID3D11Texture2D Nv12, long Time100ns);

/// <summary>
/// H.264 encoder on Media Foundation. Prefers hardware MFTs (async, fed with GPU textures through the DXGI
/// device manager) and falls back to system-memory input, then to the Microsoft software encoder.
/// </summary>
public sealed class MfH264Encoder : IDisposable
{
    private const int ProfileHigh = 100;
    private const int ProfileMain = 77;
    private const int ProfileBaseline = 66;
    private const uint InterlaceProgressive = 2;
    private const uint TransferMatrixBT709 = 1;
    private const uint NominalRange16_235 = 2;

    private static int _mfStarted;

    private readonly IMFTransform _mft;
    private readonly IMFMediaEventGenerator? _events;
    private readonly IMFDXGIDeviceManager? _manager;
    private readonly CodecApi? _codecApi;
    private readonly Action<ID3D11Texture2D, IntPtr> _readBack;
    private readonly int _width, _height, _fps;
    private volatile bool _forceKeyframe = true;

    public string Name { get; }
    public bool IsHardware { get; }
    public bool UsesGpuInput => _manager is not null;
    private bool IsAsync => _events is not null;

    private MfH264Encoder(IMFTransform mft, string name, bool hardware, IMFDXGIDeviceManager? manager,
        int width, int height, int fps, int bitrate, Action<ID3D11Texture2D, IntPtr> readBack)
    {
        _mft = mft;
        Name = name;
        IsHardware = hardware;
        _width = width;
        _height = height;
        _fps = fps;
        _readBack = readBack;

        if (hardware && mft.Attributes.GetUInt32(TransformAttributeKeys.TransformAsync) != 0)
        {
            mft.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            _events = mft.QueryInterface<IMFMediaEventGenerator>();
        }

        if (manager is not null)
        {
            mft.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)manager.NativePointer);
            _manager = manager;
        }

        _codecApi = CodecApi.From(mft);
        if (_codecApi is not null)
        {
            _codecApi.SetBool(CodecApi.AVLowLatencyMode, true);
            if (!_codecApi.SetUInt32(CodecApi.AVEncCommonRateControlMode, CodecApi.RateControlLowDelayVbr))
                _codecApi.SetUInt32(CodecApi.AVEncCommonRateControlMode, CodecApi.RateControlCbr);
            _codecApi.SetUInt32(CodecApi.AVEncCommonMeanBitRate, (uint)bitrate);
            _codecApi.SetUInt32(CodecApi.AVEncMPVDefaultBPictureCount, 0);
            // Long GOP: keyframes are requested explicitly by the tablet; this is only a safety net.
            _codecApi.SetUInt32(CodecApi.AVEncMPVGOPSize, (uint)(fps * 10));
            _codecApi.SetUInt32(CodecApi.AVEncCommonQualityVsSpeed, 30);
        }

        var stage = "tipo de saída";
        try
        {
            SetOutputType(bitrate);
            stage = "tipo de entrada";
            SetInputType();
            stage = "início do stream";
            // Optional for sync MFTs (the Microsoft software encoder rejects them), required for async ones.
            foreach (var message in new[] { TMessageType.MessageNotifyBeginStreaming, TMessageType.MessageNotifyStartOfStream })
            {
                try
                {
                    mft.ProcessMessage(message, UIntPtr.Zero);
                }
                catch (SharpGenException) when (_events is null)
                {
                }
            }
        }
        catch (SharpGenException ex)
        {
            throw new InvalidOperationException($"{stage}: 0x{ex.HResult:X8}", ex);
        }
    }

    /// <summary>Tries every suitable encoder until one accepts the configuration.</summary>
    public static MfH264Encoder Create(ID3D11Device device, int width, int height, int fps, int bitrate,
        Action<ID3D11Texture2D, IntPtr> readBack, Action<string> log, bool allowHardware = true)
    {
        if (Interlocked.Exchange(ref _mfStarted, 1) == 0)
            MediaFactory.MFStartup(true).CheckError();

        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };

        var attempts = new List<(bool Hardware, bool Gpu)>();
        if (allowHardware) attempts.AddRange([(true, true), (true, false)]);
        attempts.Add((false, false));

        foreach (var (hardware, gpu) in attempts)
        {
            var flags = (hardware ? EnumFlag.EnumFlagHardware : EnumFlag.EnumFlagSyncmft) | EnumFlag.EnumFlagSortandfilter;
            using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, (uint)flags, input, output);
            foreach (var activate in activates)
            {
                var name = TryGetName(activate);
                IMFTransform? mft = null;
                IMFDXGIDeviceManager? manager = null;
                try
                {
                    mft = activate.ActivateObject<IMFTransform>();
                    if (gpu)
                    {
                        if (mft.Attributes.GetUInt32(TransformAttributeKeys.D3D11Aware) == 0) throw new NotSupportedException("não é D3D11-aware");
                        manager = MediaFactory.MFCreateDXGIDeviceManager();
                        manager.ResetDevice(device).CheckError();
                    }
                    var encoder = new MfH264Encoder(mft, name, hardware, manager, width, height, fps, bitrate, readBack);
                    log($"Encoder: {name} ({(hardware ? "hardware" : "software")}, entrada {(gpu ? "GPU" : "memória")})");
                    return encoder;
                }
                catch (Exception ex)
                {
                    log($"Encoder {name} ({(gpu ? "GPU" : "memória")}) recusado: {ex.Message}");
                    manager?.Dispose();
                    mft?.Dispose();
                    try { activate.ShutdownObject(); } catch { }
                }
            }
        }
        throw new InvalidOperationException("Nenhum encoder H.264 disponível");
    }

    private static string TryGetName(IMFActivate activate)
    {
        try { return activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); }
        catch { return "H.264 MFT"; }
    }

    private void SetOutputType(int bitrate)
    {
        Exception? last = null;
        foreach (var profile in new[] { ProfileHigh, ProfileMain, ProfileBaseline })
        {
            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            type.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            type.Set(MediaTypeAttributeKeys.FrameSize, Pack(_width, _height));
            type.Set(MediaTypeAttributeKeys.FrameRate, Pack(_fps, 1));
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            type.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive);
            type.Set(MediaTypeAttributeKeys.Mpeg2Profile, (uint)profile);
            type.Set(MediaTypeAttributeKeys.YuvMatrix, TransferMatrixBT709);
            type.Set(MediaTypeAttributeKeys.VideoNominalRange, NominalRange16_235);
            try
            {
                _mft.SetOutputType(0, type, 0);
                return;
            }
            catch (SharpGenException ex)
            {
                last = ex;
            }
        }
        throw last!;
    }

    private void SetInputType()
    {
        using var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        type.Set(MediaTypeAttributeKeys.FrameSize, Pack(_width, _height));
        type.Set(MediaTypeAttributeKeys.FrameRate, Pack(_fps, 1));
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        type.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive);
        type.Set(MediaTypeAttributeKeys.YuvMatrix, TransferMatrixBT709);
        type.Set(MediaTypeAttributeKeys.VideoNominalRange, NominalRange16_235);
        _mft.SetInputType(0, type, 0);
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    /// <summary>The next encoded frame will be an IDR.</summary>
    public void RequestKeyframe() => _forceKeyframe = true;

    /// <summary>
    /// Encoding loop. <paramref name="pull"/> blocks until the next frame is due and returns null to stop.
    /// </summary>
    public void Run(Func<EncoderInput?> pull, Action<EncodedFrame> output, CancellationToken ct)
    {
        if (IsAsync) RunAsyncMft(pull, output, ct);
        else RunSyncMft(pull, output, ct);
    }

    private void RunSyncMft(Func<EncoderInput?> pull, Action<EncodedFrame> output, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (pull() is not { } input) return;
            Feed(input);
            while (TryProcessOutput(out var frame))
            {
                if (frame is { } f) output(f);
            }
        }
    }

    private void RunAsyncMft(Func<EncoderInput?> pull, Action<EncodedFrame> output, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var ev = _events!.GetEvent(0);
            switch (ev.EventType)
            {
                case MediaEventTypes.TransformNeedInput:
                    if (pull() is not { } input) return;
                    Feed(input);
                    break;
                case MediaEventTypes.TransformHaveOutput:
                    if (TryProcessOutput(out var frame) && frame is { } f) output(f);
                    break;
            }
        }
    }

    private void Feed(EncoderInput input)
    {
        if (_forceKeyframe)
        {
            _forceKeyframe = false;
            _codecApi?.SetUInt32(CodecApi.AVEncVideoForceKeyFrame, 1);
        }

        using var sample = MediaFactory.MFCreateSample();
        IMFMediaBuffer buffer;
        if (UsesGpuInput)
        {
            buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, input.Nv12, 0, false);
            using var buffer2d = buffer.QueryInterface<IMF2DBuffer>();
            buffer.CurrentLength = buffer2d.ContiguousLength;
        }
        else
        {
            var size = _width * _height * 3 / 2;
            buffer = MediaFactory.MFCreateMemoryBuffer(size);
            buffer.Lock(out var ptr, out _, out _);
            try { _readBack(input.Nv12, ptr); }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = size;
        }

        using (buffer) sample.AddBuffer(buffer);
        sample.SampleTime = input.Time100ns;
        sample.SampleDuration = 10_000_000 / _fps;
        _mft.ProcessInput(0, sample, 0);
    }

    /// <returns>False when the MFT needs more input. True with a null frame when the output type changed.</returns>
    private bool TryProcessOutput(out EncodedFrame? frame)
    {
        frame = null;
        var info = _mft.GetOutputStreamInfo(0);
        var providesSamples = (info.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples
                                                  | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;

        IMFSample? own = null;
        var data = new OutputDataBuffer { StreamID = 0 };
        if (!providesSamples)
        {
            own = MediaFactory.MFCreateSample();
            using var buf = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, _width * _height * 3 / 2));
            own.AddBuffer(buf);
            data.Sample = own;
        }

        try
        {
            var hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _);
            if (hr == Vortice.MediaFoundation.ResultCode.TransformNeedMoreInput) return false;
            if (hr == Vortice.MediaFoundation.ResultCode.TransformStreamChange)
            {
                using var type = _mft.GetOutputAvailableType(0, 0);
                _mft.SetOutputType(0, type, 0);
                return true;
            }
            hr.CheckError();

            var sample = own ?? data.Sample;
            using var contiguous = sample.ConvertToContiguousBuffer();
            contiguous.Lock(out var ptr, out _, out var length);
            byte[] bytes;
            try
            {
                bytes = new byte[length];
                System.Runtime.InteropServices.Marshal.Copy(ptr, bytes, 0, length);
            }
            finally
            {
                contiguous.Unlock();
            }

            frame = new EncodedFrame(bytes, sample.SampleTime / 10, Core.Video.NalUtil.ContainsIdr(bytes));
            return true;
        }
        finally
        {
            data.Events?.Dispose();
            // When we supplied the sample, data.Sample wraps the same pointer without an extra reference.
            if (own is not null) own.Dispose();
            else data.Sample?.Dispose();
        }
    }

    public void Dispose()
    {
        try
        {
            _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
            _mft.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
        }
        catch
        {
            // Best effort while shutting down.
        }
        _codecApi?.Dispose();
        _events?.Dispose();
        _mft.Dispose();
        _manager?.Dispose();
    }
}
