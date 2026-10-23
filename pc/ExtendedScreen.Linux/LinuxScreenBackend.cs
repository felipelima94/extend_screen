using System.ComponentModel;
using ExtendedScreen.Core.Input;
using ExtendedScreen.Core.Protocol;
using ExtendedScreen.Linux.Input;
using ExtendedScreen.Linux.Mutter;
using ExtendedScreen.Linux.Video;

namespace ExtendedScreen.Linux;

/// <summary>
/// GNOME/Wayland: Mutter creates a virtual monitor at the tablet's resolution (or mirrors one), PipeWire + GStreamer
/// encode it, Mutter injects the mouse and a uinput tablet carries the pen.
/// </summary>
public sealed class LinuxScreenBackend : IScreenBackend
{
    public IScreenTarget Open(HelloMessage hello, AppSettings settings, Action<string> log)
    {
        var mirror = settings.DisplayDeviceName;
        var before = TryGetMonitors(log);
        var session = MutterSession.StartAsync(settings.DrawCursor, mirror, CancellationToken.None).GetAwaiter().GetResult();
        log(mirror is null
            ? $"Monitor virtual criado ({hello.Width}x{hello.Height}); posicione-o em Configurações › Telas"
            : $"Espelhando {mirror}");

        UinputPen? pen = null;
        try
        {
            var dpi = Math.Max(1, hello.DensityDpi);
            pen = new UinputPen(hello.Width * 25.4 / dpi, hello.Height * 25.4 / dpi);
            TabletMapping.MapToWholeDesktop(log);
        }
        catch (Win32Exception ex)
        {
            log($"Caneta sem pressão ({ex.Message}). Para pressão, instale a regra udev: scripts/linux/setup.sh");
        }
        catch (Exception ex)
        {
            log($"Caneta sem pressão: {ex.Message}");
        }

        return new Target(session, pen, hello, settings, before.Select(m => m.Connector).ToHashSet(), log);
    }

    internal static List<MonitorInfo> TryGetMonitors(Action<string> log)
    {
        try
        {
            return DisplayConfig.GetMonitorsAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log($"Não foi possível ler os monitores: {ex.Message}");
            return [];
        }
    }

    private sealed class Target : IScreenTarget
    {
        private readonly MutterSession _session;
        private readonly UinputPen? _pen;
        private readonly LinuxInputInjector _input;
        private readonly HelloMessage _hello;
        private readonly AppSettings _settings;
        private readonly HashSet<string> _connectorsBefore;
        private readonly Action<string> _log;
        private volatile GstPipeline? _pipeline;
        private volatile string? _penConnector;

        public Target(MutterSession session, UinputPen? pen, HelloMessage hello, AppSettings settings,
            HashSet<string> connectorsBefore, Action<string> log)
        {
            _session = session;
            _pen = pen;
            _hello = hello;
            _settings = settings;
            _connectorsBefore = connectorsBefore;
            _log = log;
            _input = new LinuxInputInjector(session, pen, PenMapper, log);
        }

        public void RunVideo(IVideoSink sink, CancellationToken ct)
        {
            (int, int)? size = _session.IsVirtual ? (_hello.Width, _hello.Height) : null;
            using var pipeline = new GstPipeline(_session.NodeId, size, _settings.Fps, _settings.BitrateMbps * 1_000_000,
                _settings.AllowHardwareEncoder);
            _log($"Encoder: {pipeline.EncoderName}");
            _pipeline = pipeline;
            try
            {
                pipeline.Run(sink, (w, h) =>
                {
                    _log($"Capturando {w}x{h}");
                    _input.SetStreamSize(w, h);
                    // The virtual monitor only exists once PipeWire has negotiated, i.e. now.
                    if (_pen is not null) FindPenMonitor();
                }, ct);
            }
            finally
            {
                _pipeline = null;
            }
        }

        private void FindPenMonitor()
        {
            var monitors = TryGetMonitors(_log);
            var target = _session.IsVirtual
                ? monitors.LastOrDefault(m => m.IsVirtual && !_connectorsBefore.Contains(m.Connector))
                : monitors.FirstOrDefault(m => m.Connector == _settings.DisplayDeviceName);
            _penConnector = target?.Connector;
            if (target is null) _log("Monitor da caneta não encontrado; a caneta pode cobrir a área errada");
            else _log($"Caneta no monitor {target.Connector}");
        }

        /// <summary>Called on the input thread when the pen comes into range: places the monitor on the current desktop layout.</summary>
        private CoordinateMapper? PenMapper()
        {
            if (_penConnector is not { } connector) return null;
            var monitors = TryGetMonitors(_log);
            var monitor = monitors.FirstOrDefault(m => m.Connector == connector)?.Layout;
            var desktop = DisplayConfig.GetDesktop(monitors);
            return monitor is { } m && desktop is { } d ? new CoordinateMapper(m, d) : null;
        }

        public void RequestKeyframe() => _pipeline?.RequestKeyframe();

        public void Enqueue(PointerMessage msg) => _input.Enqueue(msg);

        public void Dispose()
        {
            _input.Dispose();
            _session.Dispose();
        }
    }
}
