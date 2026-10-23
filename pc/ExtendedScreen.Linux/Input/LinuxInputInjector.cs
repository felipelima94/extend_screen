using System.Collections.Concurrent;
using ExtendedScreen.Core.Input;
using ExtendedScreen.Core.Protocol;
using ExtendedScreen.Linux.Mutter;

namespace ExtendedScreen.Linux.Input;

/// <summary>
/// Injects tablet pointer events on a dedicated thread so network reads are never blocked. The first finger is the
/// mouse (through Mutter, in stream pixels); the pen goes to a uinput tablet, or to the mouse when uinput is unavailable.
/// </summary>
public sealed class LinuxInputInjector : IDisposable
{
    /// <summary>Tablets without hover never send hover-exit; drop the pen out of range after this idle time.</summary>
    private static readonly TimeSpan PenIdleTimeout = TimeSpan.FromMilliseconds(600);

    private readonly MutterSession _session;
    private readonly Action<string> _log;
    private readonly BlockingCollection<PointerMessage> _queue = new(boundedCapacity: 512);
    private readonly Thread _thread;
    private readonly UinputPen? _pen;
    private readonly Func<CoordinateMapper?> _penMapper;
    private CoordinateMapper? _mapper;
    private uint? _activeFinger;
    private bool _penAsMouseDown;
    private volatile int _width, _height;

    /// <param name="penMapper">Current placement of the target monitor on the desktop, or null while unknown.</param>
    public LinuxInputInjector(MutterSession session, UinputPen? pen, Func<CoordinateMapper?> penMapper, Action<string> log)
    {
        _session = session;
        _pen = pen;
        _penMapper = penMapper;
        _log = log;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Input", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Size of the captured stream; pointer coordinates are in its pixels. Input is dropped until known.</summary>
    public void SetStreamSize(int width, int height) => (_width, _height) = (width, height);

    public void Enqueue(PointerMessage msg) => _queue.TryAdd(msg);

    private void Loop()
    {
        while (!_queue.IsCompleted)
        {
            PointerMessage msg;
            try
            {
                if (!_queue.TryTake(out msg, _pen?.InRange == true ? PenIdleTimeout : Timeout.InfiniteTimeSpan))
                {
                    _pen?.LeaveRange();
                    continue;
                }
            }
            catch (InvalidOperationException)
            {
                break; // completed
            }

            try
            {
                Handle(msg);
            }
            catch (Exception ex)
            {
                _log($"Erro ao injetar entrada: {ex.Message}");
            }
        }
    }

    private void Handle(PointerMessage msg)
    {
        if (msg.Tool != PointerTool.Finger)
        {
            if (_pen is null)
            {
                PenAsMouse(msg);
                return;
            }
            // Monitors can be rearranged at any time; refresh the layout whenever the pen comes into range.
            if (_mapper is null || (!_pen.InRange && msg.Action is PointerAction.Hover or PointerAction.Down or PointerAction.Move))
                _mapper = _penMapper() ?? _mapper;
            if (_mapper is not null) _pen.Handle(msg, _mapper);
            return;
        }

        switch (msg.Action)
        {
            case PointerAction.Down when _activeFinger is null:
                _activeFinger = msg.PointerId;
                Move(msg);
                _session.PointerButton(MutterSession.BtnLeft, true);
                break;
            case PointerAction.Move when _activeFinger == msg.PointerId:
                Move(msg);
                break;
            case PointerAction.Up or PointerAction.Cancel when _activeFinger == msg.PointerId:
                Move(msg);
                _session.PointerButton(MutterSession.BtnLeft, false);
                _activeFinger = null;
                break;
        }
    }

    /// <summary>Fallback without uinput: hover moves the cursor, contact holds the left button.</summary>
    private void PenAsMouse(PointerMessage msg)
    {
        Move(msg);
        var down = msg.Action is PointerAction.Down or PointerAction.Move;
        if (down != _penAsMouseDown)
        {
            _session.PointerButton(MutterSession.BtnLeft, down);
            _penAsMouseDown = down;
        }
    }

    private void Move(PointerMessage msg)
    {
        var (w, h) = (_width, _height);
        if (w == 0 || h == 0) return;
        _session.PointerMotion(Math.Clamp(msg.X, 0f, 1f) * (w - 1), Math.Clamp(msg.Y, 0f, 1f) * (h - 1));
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(1000);
        try
        {
            // Release anything still held (e.g. the tablet disconnected mid-drag).
            if (_activeFinger is not null || _penAsMouseDown) _session.PointerButton(MutterSession.BtnLeft, false);
        }
        catch { }
        _pen?.Dispose();
        _queue.Dispose();
    }
}
