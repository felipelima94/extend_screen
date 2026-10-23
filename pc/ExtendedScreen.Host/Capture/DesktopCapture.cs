using ExtendedScreen.Host.Native;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ExtendedScreen.Host.Capture;

/// <summary>Thrown when the captured display changed resolution; the pipeline must be rebuilt.</summary>
public sealed class DisplayChangedException(string message) : Exception(message);

/// <summary>
/// Captures one monitor with DXGI Desktop Duplication into a BGRA texture and draws the mouse cursor on it
/// (duplication delivers the cursor separately).
/// </summary>
public sealed class DesktopCapture : IDisposable
{
    private static readonly Result WaitTimeout = new(unchecked((int)0x887A0027));
    private static readonly Result AccessLost = new(unchecked((int)0x887A0026));

    private readonly string _deviceName;
    private readonly IDXGIAdapter1 _adapter;
    private IDXGIOutput1 _output;
    private IDXGIOutputDuplication? _duplication;
    private readonly Dictionary<IntPtr, (int X, int Y)> _hotspots = [];

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }

    /// <summary>Latest desktop image with the cursor drawn on it.</summary>
    public ID3D11Texture2D Frame { get; private set; } = null!;

    /// <summary>Monitor size as the user sees it (after rotation).</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>
    /// Rotation of the monitor. Duplication delivers the image unrotated, so for 90/270 <see cref="Frame"/>
    /// is Height x Width and the converter rotates it back.
    /// </summary>
    public ModeRotation Rotation { get; private set; }

    private bool IsSideways => Rotation is ModeRotation.Rotate90 or ModeRotation.Rotate270;

    /// <summary>Monitor origin on the virtual desktop, used to place the cursor.</summary>
    public (int X, int Y) Origin { get; private set; }

    public bool DrawCursor { get; set; } = true;

    public DesktopCapture(string deviceName)
    {
        _deviceName = deviceName;
        (_adapter, _output) = FindOutput(deviceName)
            ?? throw new InvalidOperationException($"Monitor {deviceName} não encontrado no DXGI");

        D3D11.D3D11CreateDevice(_adapter, DriverType.Unknown,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        Device = device;
        Context = context;

        // The encoder may use this device from its own threads.
        using (var mt = Device.QueryInterface<ID3D11Multithread>())
            mt.SetMultithreadProtected(true);

        CreateDuplication();
    }

    public string AdapterName => _adapter.Description1.Description;

    private static (IDXGIAdapter1, IDXGIOutput1)? FindOutput(string deviceName)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                if (output.Description.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    var output1 = output.QueryInterface<IDXGIOutput1>();
                    output.Dispose();
                    return (adapter, output1);
                }
                output.Dispose();
            }
            adapter.Dispose();
        }
        return null;
    }

    private void CreateDuplication()
    {
        _duplication?.Dispose();
        Frame?.Dispose();

        // DuplicateOutput1 also works on HDR / FP16 desktops; fall back to the classic API on older systems.
        using (var output5 = _output.QueryInterfaceOrNull<IDXGIOutput5>())
        {
            _duplication = output5 is not null
                ? output5.DuplicateOutput1(Device, [Format.B8G8R8A8_UNorm])
                : _output.DuplicateOutput(Device);
        }

        var desc = _duplication.Description;
        Width = (int)desc.ModeDescription.Width;
        Height = (int)desc.ModeDescription.Height;
        Rotation = desc.Rotation;
        var coords = _output.Description.DesktopCoordinates;
        Origin = (coords.Left, coords.Top);

        Frame = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)(IsSideways ? Height : Width),
            Height = (uint)(IsSideways ? Width : Height),
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.GdiCompatible,
        });
    }

    /// <summary>Waits for a new desktop frame and copies it into <see cref="Frame"/>.</summary>
    /// <returns>True when the frame changed, false on timeout (Frame keeps the previous image).</returns>
    public bool Acquire(int timeoutMs)
    {
        if (_duplication is null) RecreateAfterAccessLost();

        var hr = _duplication!.AcquireNextFrame((uint)timeoutMs, out var info, out var resource);
        if (hr == WaitTimeout) return false;
        if (hr == AccessLost)
        {
            RecreateAfterAccessLost();
            return false;
        }
        hr.CheckError();

        try
        {
            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            Context.CopyResource(Frame, texture);
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }

        // The cursor is drawn in desktop coordinates, which only match the texture when the monitor is not rotated.
        if (DrawCursor && Rotation is ModeRotation.Identity or ModeRotation.Unspecified) DrawCursorOnFrame();
        return true;
    }

    /// <summary>
    /// Access is lost on mode changes, UAC prompts and lock screen. Recreate the duplication;
    /// if the resolution changed the whole pipeline must be rebuilt.
    /// </summary>
    private void RecreateAfterAccessLost()
    {
        var (oldW, oldH) = (Width, Height);
        _duplication?.Dispose();
        _duplication = null;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _output.Dispose();
                (_, _output) = FindOutputOnAdapter() ?? throw new DisplayChangedException($"{_deviceName} desconectado");
                CreateDuplication();
                break;
            }
            catch (SharpGenException) when (attempt < 20)
            {
                // E_ACCESSDENIED while the secure desktop is shown: keep trying for a while.
                Thread.Sleep(250);
            }
        }

        if (Width != oldW || Height != oldH)
            throw new DisplayChangedException($"Resolução mudou para {Width}x{Height}");
    }

    private (IDXGIAdapter1, IDXGIOutput1)? FindOutputOnAdapter()
    {
        for (uint o = 0; _adapter.EnumOutputs(o, out var output).Success; o++)
        {
            if (output.Description.DeviceName.Equals(_deviceName, StringComparison.OrdinalIgnoreCase))
            {
                var output1 = output.QueryInterface<IDXGIOutput1>();
                output.Dispose();
                return (_adapter, output1);
            }
            output.Dispose();
        }
        return null;
    }

    private void DrawCursorOnFrame()
    {
        var ci = new User32.CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<User32.CURSORINFO>() };
        if (!User32.GetCursorInfo(ref ci) || (ci.flags & User32.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;

        var x = ci.ptScreenPos.X - Origin.X;
        var y = ci.ptScreenPos.Y - Origin.Y;
        if (x < -64 || y < -64 || x > Width || y > Height) return;

        if (!_hotspots.TryGetValue(ci.hCursor, out var hotspot))
        {
            if (User32.GetIconInfo(ci.hCursor, out var ii))
            {
                hotspot = (ii.xHotspot, ii.yHotspot);
                if (ii.hbmMask != IntPtr.Zero) User32.DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) User32.DeleteObject(ii.hbmColor);
            }
            _hotspots[ci.hCursor] = hotspot;
        }

        using var surface = Frame.QueryInterface<IDXGISurface1>();
        var hdc = surface.GetDC(false);
        try
        {
            User32.DrawIconEx(hdc, x - hotspot.X, y - hotspot.Y, ci.hCursor, 0, 0, 0, IntPtr.Zero, User32.DI_NORMAL);
        }
        finally
        {
            surface.ReleaseDC(null);
        }
    }

    public void Dispose()
    {
        _duplication?.Dispose();
        Frame?.Dispose();
        _output.Dispose();
        _adapter.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
