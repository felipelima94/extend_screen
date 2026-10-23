using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ExtendedScreen.Core.Input;
using ExtendedScreen.Core.Protocol;
using static ExtendedScreen.Linux.Input.UinputNative;

namespace ExtendedScreen.Linux.Input;

/// <summary>
/// A virtual pen tablet on /dev/uinput (pressure, tilt, barrel button, eraser). GNOME spreads an unmapped tablet over
/// the whole desktop (it does not honor the "output" setting for virtual monitors), so positions are placed inside the
/// target monitor's slice of the desktop with a <see cref="CoordinateMapper"/>.
/// </summary>
public sealed unsafe class UinputPen : IDisposable
{
    public const ushort VendorId = 0x1209;
    public const ushort ProductId = 0xE5C1;
    public const string DevicePath = "/dev/uinput";

    private const int AxisMax = 65535;
    private const int PressureMax = 4095;

    private readonly int _fd;
    private bool _inRange;
    private bool _inContact;
    private ushort _tool = BTN_TOOL_PEN;
    private bool _barrel;

    public bool InRange => _inRange;

    /// <param name="widthMm">Physical size of the tablet screen; libinput needs a resolution on tablet axes.</param>
    public UinputPen(double widthMm, double heightMm)
    {
        _fd = open(DevicePath, O_WRONLY | O_NONBLOCK);
        if (_fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Sem acesso a {DevicePath}");
        try
        {
            Check(ioctl(_fd, UI_SET_EVBIT, EV_KEY));
            Check(ioctl(_fd, UI_SET_EVBIT, EV_ABS));
            foreach (var key in new[] { BTN_TOOL_PEN, BTN_TOOL_RUBBER, BTN_TOUCH, BTN_STYLUS })
                Check(ioctl(_fd, UI_SET_KEYBIT, key));
            // No INPUT_PROP_DIRECT on purpose: GNOME maps "integrated" pen displays by its own heuristics and ignores the
            // tablet "output" setting, which would spread the pen over the whole desktop. An external tablet honors it.

            SetupAxis(ABS_X, 0, AxisMax, (int)Math.Max(1, Math.Round(AxisMax / Math.Max(1, widthMm))));
            SetupAxis(ABS_Y, 0, AxisMax, (int)Math.Max(1, Math.Round(AxisMax / Math.Max(1, heightMm))));
            SetupAxis(ABS_PRESSURE, 0, PressureMax, 0);
            SetupAxis(ABS_TILT_X, -90, 90, 57); // units per radian: one unit per degree
            SetupAxis(ABS_TILT_Y, -90, 90, 57);

            var setup = new UinputSetup { Id = new InputId { BusType = BUS_VIRTUAL, Vendor = VendorId, Product = ProductId, Version = 1 } };
            var name = Encoding.ASCII.GetBytes("ExtendedScreen Pen");
            for (var i = 0; i < name.Length; i++) setup.Name[i] = name[i];
            Check(ioctl(_fd, UI_DEV_SETUP, &setup));
            Check(ioctl(_fd, UI_DEV_CREATE, 0));
        }
        catch
        {
            close(_fd);
            throw;
        }
    }

    private void SetupAxis(ushort code, int min, int max, int resolution)
    {
        var abs = new UinputAbsSetup { Code = code, Minimum = min, Maximum = max, Resolution = resolution };
        Check(ioctl(_fd, UI_ABS_SETUP, &abs));
    }

    private static void Check(int result)
    {
        if (result < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public void Handle(PointerMessage msg, CoordinateMapper mapper)
    {
        var tool = msg.Tool == PointerTool.Eraser ? BTN_TOOL_RUBBER : BTN_TOOL_PEN;
        if (_inRange && tool != _tool) LeaveRange(); // switching ends flips the tool; it must leave proximity first

        switch (msg.Action)
        {
            case PointerAction.Hover:
                if (_inContact) Contact(false, 0);
                EnterRange(tool);
                Position(msg, mapper);
                Pressure(0);
                Sync();
                break;

            case PointerAction.Down:
            case PointerAction.Move:
                EnterRange(tool);
                Position(msg, mapper);
                Pressure(Math.Clamp((int)(msg.Pressure * PressureMax), 1, PressureMax));
                if (!_inContact) Contact(true, 1);
                Sync();
                break;

            case PointerAction.Up:
                if (_inContact)
                {
                    Position(msg, mapper);
                    Pressure(0);
                    Contact(false, 0);
                    Sync();
                }
                break;

            case PointerAction.HoverExit:
            case PointerAction.Cancel:
                LeaveRange();
                break;
        }

        if (msg.BarrelButton != _barrel && _inRange)
        {
            _barrel = msg.BarrelButton;
            Emit(EV_KEY, BTN_STYLUS, _barrel ? 1 : 0);
            Sync();
        }
    }

    /// <summary>Takes the pen out of proximity; used when the tablet stops sending hover events.</summary>
    public void LeaveRange()
    {
        if (!_inRange) return;
        if (_inContact)
        {
            Pressure(0);
            Contact(false, 0);
        }
        if (_barrel)
        {
            Emit(EV_KEY, BTN_STYLUS, 0);
            _barrel = false;
        }
        Emit(EV_KEY, _tool, 0);
        Sync();
        _inRange = false;
    }

    private void EnterRange(ushort tool)
    {
        if (_inRange) return;
        _tool = tool;
        Emit(EV_KEY, tool, 1);
        _inRange = true;
    }

    private void Position(PointerMessage msg, CoordinateMapper mapper)
    {
        // Same 0..65535-over-the-desktop scale as SendInput's absolute coordinates.
        var (x, y) = mapper.ToSendInputAbsolute(msg.X, msg.Y);
        Emit(EV_ABS, ABS_X, x);
        Emit(EV_ABS, ABS_Y, y);
        Emit(EV_ABS, ABS_TILT_X, Math.Clamp((int)MathF.Round(msg.TiltX), -90, 90));
        Emit(EV_ABS, ABS_TILT_Y, Math.Clamp((int)MathF.Round(msg.TiltY), -90, 90));
    }

    private void Pressure(int value) => Emit(EV_ABS, ABS_PRESSURE, value);

    private void Contact(bool down, int value)
    {
        Emit(EV_KEY, BTN_TOUCH, value);
        _inContact = down;
    }

    private void Sync() => Emit(EV_SYN, SYN_REPORT, 0);

    private void Emit(ushort type, ushort code, int value)
    {
        var ev = new InputEvent { Type = type, Code = code, Value = value };
        write(_fd, &ev, (nuint)sizeof(InputEvent));
    }

    public void Dispose()
    {
        try { LeaveRange(); } catch { }
        ioctl(_fd, UI_DEV_DESTROY, 0);
        close(_fd);
    }
}
