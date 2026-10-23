using System.Globalization;
using ExtendedScreen.Core.Protocol;
using ExtendedScreen.Linux;
using ExtendedScreen.Linux.Input;
using ExtendedScreen.Linux.Mutter;
using ExtendedScreen.Linux.Video;
using ExtendedScreen.Server.Adb;
using ExtendedScreen.Server.Streaming;

Log.EchoToConsole = true;

string? Opt(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// Command-line overrides apply to this run only; settings.json keeps the defaults.
var settings = AppSettings.Load();
if (Opt("--fps") is { } fps) settings.Fps = int.Parse(fps, CultureInfo.InvariantCulture);
if (Opt("--bitrate") is { } mbps) settings.BitrateMbps = int.Parse(mbps, CultureInfo.InvariantCulture);
if (Opt("--mirror") is { } connector) settings.DisplayDeviceName = connector;
if (args.Contains("--no-cursor")) settings.DrawCursor = false;
if (args.Contains("--software")) settings.AllowHardwareEncoder = false;

switch (args.FirstOrDefault(a => a is "--check" or "--list-displays" or "--record" or "--help" or "-h"))
{
    case "--check":
        return await Check();
    case "--list-displays":
        foreach (var m in await DisplayConfig.GetMonitorsAsync()) Console.WriteLine(m);
        return 0;
    case "--record":
        return Record();
    case "--help" or "-h":
        Usage();
        return 0;
    default:
        return Serve();
}

int Serve()
{
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };

    using var adb = new AdbManager(ProtocolConstants.Port);
    adb.StatusChanged += Log.Write;
    using var server = new StreamServer(ProtocolConstants.Port, new LinuxScreenBackend(), () => settings, Log.Write);
    server.Start();
    adb.Start();
    Log.Write($"ExtendedScreen rodando ({settings.Fps} fps, {settings.BitrateMbps} Mbps). Abra o app no tablet. Ctrl+C para sair.");
    stop.Token.WaitHandle.WaitOne();
    Log.Write("Encerrando…");
    return 0;
}

/// <summary>Encodes a new virtual monitor (or a mirrored one) to a raw .h264 file, to test capture and encoding without a tablet.</summary>
int Record()
{
    var path = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : "capture.h264";
    var seconds = double.Parse(Opt("--seconds") ?? "5", CultureInfo.InvariantCulture);
    var width = int.Parse(Opt("--width") ?? "1920", CultureInfo.InvariantCulture);
    var height = int.Parse(Opt("--height") ?? "1080", CultureInfo.InvariantCulture);

    using var session = MutterSession.StartAsync(settings.DrawCursor, settings.DisplayDeviceName, CancellationToken.None).GetAwaiter().GetResult();
    using var pipeline = new GstPipeline(session.NodeId, session.IsVirtual ? (width, height) : null, settings.Fps,
        settings.BitrateMbps * 1_000_000, settings.AllowHardwareEncoder);
    Log.Write($"Encoder: {pipeline.EncoderName}");

    using var file = File.Create(path);
    var sink = new FileSink(file);
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    pipeline.Run(sink, (_, _) => { }, cts.Token);
    Console.WriteLine($"{sink.Frames} frames ({sink.Keyframes} keyframes), {file.Length / 1024} KiB, {sink.Width}x{sink.Height} → {Path.GetFullPath(path)}");
    return sink.Frames > 0 ? 0 : 1;
}

async Task<int> Check()
{
    var ok = true;
    void Report(bool pass, string what, string detail)
    {
        Console.WriteLine($"[{(pass ? " ok " : "FALHA")}] {what}: {detail}");
        ok &= pass;
    }

    try
    {
        Report(true, "GNOME/Mutter ScreenCast", $"versão {await MutterSession.GetVersionAsync()}");
    }
    catch (Exception ex)
    {
        Report(false, "GNOME/Mutter ScreenCast", $"{ex.Message} (é preciso uma sessão GNOME no Wayland)");
    }

    try
    {
        var encoder = GstPipeline.DescribeAvailableEncoder(settings.AllowHardwareEncoder);
        Report(encoder is not null && GstNative.HasElement("pipewiresrc"), "GStreamer",
            encoder is null ? "nenhum encoder H.264 (instale gstreamer1-plugin-openh264)" : $"encoder {encoder}");
        if (encoder is not null && !encoder.Contains("GPU"))
            Console.WriteLine("        Dica: para encode na GPU AMD/Intel, instale mesa-va-drivers-freeworld (RPM Fusion).");
    }
    catch (Exception ex)
    {
        Report(false, "GStreamer", ex.Message);
    }

    try
    {
        using var pen = new UinputPen(200, 120);
        Report(true, "Caneta (uinput)", "pressão e inclinação disponíveis");
    }
    catch (Exception ex)
    {
        Report(false, "Caneta (uinput)", $"{ex.Message}; rode scripts/linux/setup.sh (sem isso a caneta vira mouse)");
    }

    var adb = AdbManager.FindAdb();
    Report(adb is not null, "adb", adb ?? "não encontrado (sudo dnf install android-tools)");
    return ok ? 0 : 1;
}

void Usage() => Console.WriteLine("""
    Uso:
      extendedscreen [opções]                     servidor: cria um monitor virtual para o tablet (Ctrl+C sai)
      extendedscreen --check                      verifica GNOME, GStreamer, uinput e adb
      extendedscreen --list-displays              lista os monitores
      extendedscreen --record out.h264 [--seconds 5] [--width 1920 --height 1080] [opções]

    Opções:
      --fps N            quadros por segundo (padrão do settings.json)
      --bitrate N        Mbps
      --mirror CONECTOR  espelha um monitor existente (ex.: DP-1) em vez de criar um virtual
      --no-cursor        não desenha o cursor do mouse
      --software         não usa o encoder da GPU
    """);

sealed class FileSink(Stream stream) : IVideoSink
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
