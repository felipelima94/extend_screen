using System.Runtime.InteropServices;
using ExtendedScreen.Host.Display;
using ExtendedScreen.Host.Encoding;
using ExtendedScreen.Host.Streaming;

namespace ExtendedScreen.Host;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        // Physical pixels everywhere: capture, cursor position and input injection must agree.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        if (args.Length > 0)
        {
            AttachConsole(-1);
            Log.EchoToConsole = true;
            Console.WriteLine();
            return RunCli(args);
        }

        using var mutex = new Mutex(true, "ExtendedScreen.Host.SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("O ExtendedScreen já está rodando (veja o ícone na bandeja).", "ExtendedScreen");
            return 1;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
        return 0;
    }

    private static int RunCli(string[] args)
    {
        switch (args[0])
        {
            case "--list-displays":
                foreach (var d in DisplayFinder.GetActiveDisplays())
                    Console.WriteLine($"{d.DeviceName}\t{d.AdapterName}\t{d.Bounds}\t{(d.IsVirtual ? "virtual" : "")}{(d.IsPrimary ? " primary" : "")}");
                return 0;

            case "--record":
                return Record(args);

            default:
                Console.WriteLine("""
                    Uso:
                      ExtendedScreen.Host                       app de bandeja
                      ExtendedScreen.Host --list-displays       lista os monitores
                      ExtendedScreen.Host --record out.h264 [--seconds 5] [--display \\.\DISPLAY1] [--fps 60] [--software]
                    """);
                return 2;
        }
    }

    /// <summary>Encodes a monitor to a raw .h264 file (playable with ffplay/VLC) to test capture and encoding without a tablet.</summary>
    private static int Record(string[] args)
    {
        string Opt(string name, string fallback)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        var path = args.Length > 1 ? args[1] : "capture.h264";
        var seconds = double.Parse(Opt("--seconds", "5"), System.Globalization.CultureInfo.InvariantCulture);
        var fps = int.Parse(Opt("--fps", "60"));
        var display = Opt("--display", DisplayFinder.FindTarget(null)?.DeviceName
                                       ?? DisplayFinder.GetActiveDisplays().First(d => d.IsPrimary).DeviceName);
        var hardware = !args.Contains("--software");

        using var file = File.Create(path);
        var sink = new FileSink(file);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        new VideoPipeline(display, fps, 20_000_000, drawCursor: true, hardware, Log.Write).Run(sink, cts.Token);
        Console.WriteLine($"{sink.Frames} frames ({sink.Keyframes} keyframes), {file.Length / 1024} KiB, {sink.Width}x{sink.Height} → {Path.GetFullPath(path)}");
        return sink.Frames > 0 ? 0 : 1;
    }

    private sealed class FileSink(Stream stream) : IVideoSink
    {
        public int Frames, Keyframes, Width, Height;

        public void OnConfig(int width, int height) => (Width, Height) = (width, height);

        public void OnCodecConfig(byte[] spsPps) { }

        public void OnFrame(EncodedFrame frame)
        {
            Frames++;
            if (frame.Keyframe) Keyframes++;
            stream.Write(frame.Data);
        }
    }
}
