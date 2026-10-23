using ExtendedScreen.Core.Protocol;
using ExtendedScreen.Host.Display;
using ExtendedScreen.Host.Input;
using ExtendedScreen.Host.Streaming;

namespace ExtendedScreen.Host;

/// <summary>Streams a (usually virtual) Windows monitor with DXGI + Media Foundation and injects input with SendInput / synthetic pen.</summary>
public sealed class WindowsScreenBackend : IScreenBackend
{
    public IScreenTarget Open(HelloMessage hello, AppSettings settings, Action<string> log)
    {
        var display = DisplayFinder.FindTarget(settings.DisplayDeviceName)
            ?? throw new InvalidOperationException(
                "Nenhum monitor virtual encontrado. Instale o Virtual Display Driver ou escolha um monitor no menu da bandeja.");

        if (settings.MatchTabletResolution && display.IsVirtual)
        {
            if (DisplayFinder.TrySetResolution(display.DeviceName, hello.Width, hello.Height))
                log($"{display.DeviceName} ajustado para {hello.Width}x{hello.Height}");
            else if (DisplayFinder.PickClosestMode(display.DeviceName, hello.Width, hello.Height) is { } mode
                     && DisplayFinder.TrySetResolution(display.DeviceName, mode.Width, mode.Height))
                log($"O driver não oferece {hello.Width}x{hello.Height}; usando {mode.Width}x{mode.Height}. " +
                    "Para a resolução exata, adicione-a em vdd_settings.xml do Virtual Display Driver.");
            else
                log($"O driver não oferece {hello.Width}x{hello.Height}. Adicione essa resolução em vdd_settings.xml do Virtual Display Driver.");
        }

        return new Target(display.DeviceName, settings, log);
    }

    private sealed class Target(string deviceName, AppSettings settings, Action<string> log) : IScreenTarget
    {
        private readonly InputInjector _input = new(deviceName, log);
        private readonly VideoPipeline _pipeline = new(deviceName, settings.Fps, settings.BitrateMbps * 1_000_000,
            settings.DrawCursor, settings.AllowHardwareEncoder, log);

        public void RunVideo(IVideoSink sink, CancellationToken ct) => _pipeline.Run(sink, ct);

        public void RequestKeyframe() => _pipeline.RequestKeyframe();

        public void Enqueue(PointerMessage msg) => _input.Enqueue(msg);

        public void Dispose() => _input.Dispose();
    }
}
