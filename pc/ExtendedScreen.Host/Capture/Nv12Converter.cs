using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ExtendedScreen.Host.Capture;

/// <summary>
/// Converts the BGRA desktop texture to NV12 (BT.709, limited range) on the GPU with the D3D11 video processor.
/// Output goes into a small ring of textures so the encoder can still be reading the previous ones.
/// </summary>
public sealed class Nv12Converter : IDisposable
{
    public const int RingSize = 4;

    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11VideoProcessorInputView _inputView;
    private readonly ID3D11Texture2D[] _ring = new ID3D11Texture2D[RingSize];
    private readonly ID3D11VideoProcessorOutputView[] _outputViews = new ID3D11VideoProcessorOutputView[RingSize];
    private ID3D11Texture2D? _staging;
    private int _next;

    public int Width { get; }
    public int Height { get; }

    /// <param name="width">Output width (the monitor as seen by the user).</param>
    /// <param name="rotation">Monitor rotation; the source texture is unrotated and gets rotated here.</param>
    public Nv12Converter(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D source, int width, int height,
        ModeRotation rotation = ModeRotation.Identity)
    {
        _context = context;
        var srcDesc = source.Description;
        Width = width & ~1;
        Height = height & ~1;

        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = context.QueryInterface<ID3D11VideoContext>();

        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = srcDesc.Width,
            InputHeight = srcDesc.Height,
            InputFrameRate = new Rational(60, 1),
            OutputWidth = (uint)Width,
            OutputHeight = (uint)Height,
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.OptimalSpeed,
        });
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace { RGB_Range = 0 });
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace { YCbCr_Matrix = 1, Nominal_Range = 1 });
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        var vpRotation = rotation switch
        {
            ModeRotation.Rotate90 => VideoProcessorRotation.Rotation90,
            ModeRotation.Rotate180 => VideoProcessorRotation.Rotation180,
            ModeRotation.Rotate270 => VideoProcessorRotation.Rotation270,
            _ => VideoProcessorRotation.Identity,
        };
        if (vpRotation != VideoProcessorRotation.Identity)
            _videoContext.VideoProcessorSetStreamRotation(_processor, 0, true, vpRotation);

        _inputView = _videoDevice.CreateVideoProcessorInputView(source, _enumerator, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });

        for (var i = 0; i < RingSize; i++)
        {
            _ring[i] = CreateNv12Texture(device);
            _outputViews[i] = _videoDevice.CreateVideoProcessorOutputView(_ring[i], _enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });
        }
    }

    private ID3D11Texture2D CreateNv12Texture(ID3D11Device device)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.VideoEncoder,
        };
        try
        {
            return device.CreateTexture2D(desc);
        }
        catch
        {
            // Some drivers refuse VIDEO_ENCODER on render targets.
            desc.BindFlags = BindFlags.RenderTarget;
            return device.CreateTexture2D(desc);
        }
    }

    /// <summary>Converts the source texture into the next ring slot and returns that texture.</summary>
    public ID3D11Texture2D Convert()
    {
        var index = _next;
        _next = (_next + 1) % RingSize;
        var stream = new VideoProcessorStream { Enable = true, InputSurface = _inputView };
        _videoContext.VideoProcessorBlt(_processor, _outputViews[index], 0, 1, [stream]).CheckError();
        return _ring[index];
    }

    /// <summary>Copies an NV12 texture to CPU memory as tightly packed NV12 (Y plane, then interleaved UV).</summary>
    public unsafe void ReadBack(ID3D11Texture2D nv12, IntPtr destination)
    {
        _staging ??= _context.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        _context.CopyResource(_staging, nv12);
        var mapped = _context.Map(_staging, 0, MapMode.Read);
        try
        {
            var src = (byte*)mapped.DataPointer;
            var dst = (byte*)destination;
            var pitch = (int)mapped.RowPitch;
            // Y rows, then Height/2 rows of UV; the UV plane starts right after Height rows of Y in the mapping.
            for (var row = 0; row < Height + Height / 2; row++)
                Buffer.MemoryCopy(src + row * pitch, dst + row * Width, Width, Width);
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    public void Dispose()
    {
        foreach (var v in _outputViews) v?.Dispose();
        foreach (var t in _ring) t?.Dispose();
        _staging?.Dispose();
        _inputView.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
