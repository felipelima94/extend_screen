using System.Collections.Concurrent;
using ExtendedScreen.Core.Input;
using ExtendedScreen.Core.Protocol;
using ExtendedScreen.Host.Display;

namespace ExtendedScreen.Host.Input;

/// <summary>Injects tablet pointer events on a dedicated thread so network reads are never blocked.</summary>
public sealed class InputInjector : IDisposable
{
    /// <summary>Tablets without hover never send hover-exit; drop the pen out of range after this idle time.</summary>
    private static readonly TimeSpan PenIdleTimeout = TimeSpan.FromMilliseconds(600);

    private readonly string _deviceName;
    private readonly Action<string> _log;
    private readonly BlockingCollection<PointerMessage> _queue = new(boundedCapacity: 512);
    private readonly Thread _thread;
    private readonly MouseInjector _mouse = new();
    private PenInjector? _pen;
    private CoordinateMapper? _mapper;

    public InputInjector(string deviceName, Action<string> log)
    {
        _deviceName = deviceName;
        _log = log;
        try
        {
            _pen = new PenInjector(log);
        }
        catch (Exception ex)
        {
            log($"Caneta indisponível (requer Windows 10 1809+): {ex.Message}");
        }
        _thread = new Thread(Loop) { IsBackground = true, Name = "Input", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

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
        // Monitors can be rearranged at any time; refresh the mapping at the start of each gesture.
        if (_mapper is null || msg.Action == PointerAction.Down || (msg.Action == PointerAction.Hover && _pen?.InRange != true))
            RefreshMapper();
        if (_mapper is null) return;

        if (msg.Tool == PointerTool.Finger) _mouse.Handle(msg, _mapper);
        else _pen?.Handle(msg, _mapper);
    }

    private void RefreshMapper()
    {
        var display = DisplayFinder.GetActiveDisplays()
            .FirstOrDefault(d => d.DeviceName.Equals(_deviceName, StringComparison.OrdinalIgnoreCase));
        if (display is not null) _mapper = new CoordinateMapper(display.Bounds, DisplayFinder.GetVirtualDesktop());
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(1000);
        _mouse.Reset();
        _pen?.Dispose();
        _queue.Dispose();
    }
}
