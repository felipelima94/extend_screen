using System.Runtime.InteropServices;
using ExtendedScreen.Core.Input;
using ExtendedScreen.Core.Protocol;
using ExtendedScreen.Host.Native;

namespace ExtendedScreen.Host.Input;

/// <summary>Turns the first finger on the tablet into the left mouse button.</summary>
public sealed class MouseInjector
{
    private uint? _activeFinger;

    public void Handle(PointerMessage msg, CoordinateMapper mapper)
    {
        var (x, y) = mapper.ToSendInputAbsolute(msg.X, msg.Y);
        switch (msg.Action)
        {
            case PointerAction.Down when _activeFinger is null:
                _activeFinger = msg.PointerId;
                Send(x, y, User32.MOUSEEVENTF_MOVE);
                Send(x, y, User32.MOUSEEVENTF_LEFTDOWN);
                break;
            case PointerAction.Move when _activeFinger == msg.PointerId:
                Send(x, y, User32.MOUSEEVENTF_MOVE);
                break;
            case PointerAction.Up or PointerAction.Cancel when _activeFinger == msg.PointerId:
                Send(x, y, User32.MOUSEEVENTF_MOVE);
                Send(x, y, User32.MOUSEEVENTF_LEFTUP);
                _activeFinger = null;
                break;
        }
    }

    /// <summary>Releases the button if a finger is still down (e.g. the tablet disconnected mid-drag).</summary>
    public void Reset()
    {
        if (_activeFinger is null) return;
        Send(0, 0, User32.MOUSEEVENTF_LEFTUP, absolute: false);
        _activeFinger = null;
    }

    private static void Send(int x, int y, uint flags, bool absolute = true)
    {
        if (absolute) flags |= User32.MOUSEEVENTF_ABSOLUTE | User32.MOUSEEVENTF_VIRTUALDESK | User32.MOUSEEVENTF_MOVE;
        var input = new User32.INPUT
        {
            type = User32.INPUT_MOUSE,
            mi = new User32.MOUSEINPUT { dx = x, dy = y, dwFlags = flags },
        };
        User32.SendInput(1, [input], Marshal.SizeOf<User32.INPUT>());
    }
}
