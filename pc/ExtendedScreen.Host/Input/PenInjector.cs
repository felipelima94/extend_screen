using System.ComponentModel;
using ExtendedScreen.Core.Input;
using ExtendedScreen.Core.Protocol;
using ExtendedScreen.Host.Native;
using static ExtendedScreen.Host.Native.User32;

namespace ExtendedScreen.Host.Input;

/// <summary>Injects the tablet stylus as a native Windows pen (pressure, tilt, barrel button, eraser).</summary>
public sealed class PenInjector : IDisposable
{
    private const uint PointerId = 1;
    private const uint MaxPressure = 1024;

    private readonly IntPtr _device;
    private readonly Action<string> _log;
    private bool _inRange;
    private bool _inContact;
    private bool _loggedFailure;
    private PointerMessage _last;
    private (int X, int Y) _lastPixel;

    public PenInjector(Action<string> log)
    {
        _log = log;
        _device = CreateSyntheticPointerDevice(PT_PEN, 1, POINTER_FEEDBACK_DEFAULT);
        if (_device == IntPtr.Zero)
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "CreateSyntheticPointerDevice falhou");
    }

    public bool InRange => _inRange;

    public void Handle(PointerMessage msg, CoordinateMapper mapper)
    {
        var pixel = mapper.ToVirtualDesktopPixel(msg.X, msg.Y);
        _last = msg;
        _lastPixel = pixel;

        switch (msg.Action)
        {
            case PointerAction.Hover:
                if (_inContact) Lift(msg, pixel);
                Inject(msg, pixel, POINTER_FLAG_INRANGE | POINTER_FLAG_UPDATE);
                _inRange = true;
                break;

            case PointerAction.Down:
                EnsureInRange(msg, pixel);
                Inject(msg, pixel, POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_DOWN);
                _inContact = true;
                break;

            case PointerAction.Move:
                if (!_inContact)
                {
                    EnsureInRange(msg, pixel);
                    Inject(msg, pixel, POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_DOWN);
                    _inContact = true;
                }
                else
                {
                    Inject(msg, pixel, POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_UPDATE);
                }
                break;

            case PointerAction.Up:
                if (_inContact) Lift(msg, pixel);
                break;

            case PointerAction.HoverExit:
                LeaveRange();
                break;

            case PointerAction.Cancel:
                if (_inContact)
                {
                    Inject(msg, pixel, POINTER_FLAG_UP | POINTER_FLAG_CANCELED);
                    _inContact = false;
                }
                LeaveRange();
                break;
        }
    }

    /// <summary>Takes the pen out of range; used when the tablet stops sending hover events.</summary>
    public void LeaveRange()
    {
        if (_inContact) Lift(_last, _lastPixel);
        if (!_inRange) return;
        Inject(_last with { Pressure = 0 }, _lastPixel, POINTER_FLAG_UPDATE);
        _inRange = false;
    }

    private void EnsureInRange(PointerMessage msg, (int X, int Y) pixel)
    {
        if (_inRange) return;
        Inject(msg, pixel, POINTER_FLAG_INRANGE | POINTER_FLAG_UPDATE);
        _inRange = true;
    }

    private void Lift(PointerMessage msg, (int X, int Y) pixel)
    {
        Inject(msg, pixel, POINTER_FLAG_INRANGE | POINTER_FLAG_UP);
        _inContact = false;
        _inRange = true;
    }

    private void Inject(PointerMessage msg, (int X, int Y) pixel, uint pointerFlags)
    {
        var contact = (pointerFlags & POINTER_FLAG_INCONTACT) != 0;
        uint penFlags = 0;
        if (msg.BarrelButton) penFlags |= PEN_FLAG_BARREL;
        if (msg.Tool == PointerTool.Eraser) penFlags |= contact ? PEN_FLAG_ERASER : PEN_FLAG_INVERTED;

        var pressure = contact ? Math.Clamp((uint)(msg.Pressure * MaxPressure), 1u, MaxPressure) : 0u;

        var info = new POINTER_TYPE_INFO
        {
            type = PT_PEN,
            penInfo = new POINTER_PEN_INFO
            {
                pointerInfo = new POINTER_INFO
                {
                    pointerType = PT_PEN,
                    pointerId = PointerId,
                    pointerFlags = pointerFlags,
                    ptPixelLocation = new POINT { X = pixel.X, Y = pixel.Y },
                },
                penFlags = penFlags,
                penMask = PEN_MASK_PRESSURE | PEN_MASK_TILT_X | PEN_MASK_TILT_Y,
                pressure = pressure,
                tiltX = Math.Clamp((int)MathF.Round(msg.TiltX), -90, 90),
                tiltY = Math.Clamp((int)MathF.Round(msg.TiltY), -90, 90),
            },
        };

        if (!InjectSyntheticPointerInput(_device, [info], 1) && !_loggedFailure)
        {
            _loggedFailure = true;
            _log($"InjectSyntheticPointerInput falhou: erro {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }
    }

    public void Dispose()
    {
        try { LeaveRange(); } catch { }
        DestroySyntheticPointerDevice(_device);
    }
}
