using System.Diagnostics;
using System.Net.Sockets;
using ExtendedScreen.Core.Protocol;
using ExtendedScreen.Host.Display;
using ExtendedScreen.Host.Encoding;
using ExtendedScreen.Host.Input;

namespace ExtendedScreen.Host.Streaming;

/// <summary>One connected tablet: handshake, video out, input and ping in.</summary>
public sealed class StreamSession(TcpClient client, AppSettings settings, Action<string> status) : IVideoSink
{
    private readonly CancellationTokenSource _cts = new();
    private FrameWriter _writer = null!;
    private VideoPipeline? _pipeline;
    private readonly byte[] _frameHeader = new byte[VideoFrameMessage.HeaderSize];

    public HelloMessage? Hello { get; private set; }

    public void Stop() => _cts.Cancel();

    public async Task RunAsync(CancellationToken outer)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer, _cts.Token);
        var ct = linked.Token;
        client.NoDelay = true;
        client.SendBufferSize = 1 << 20;
        var stream = client.GetStream();
        var reader = new FrameReader(stream);
        _writer = new FrameWriter(stream);

        var first = await reader.ReadAsync(ct);
        if (first is not { Type: MessageType.Hello } helloFrame)
            throw new InvalidDataException("O tablet não enviou HELLO");
        var hello = HelloMessage.Parse(helloFrame.Payload);
        Hello = hello;
        Log.Write($"Tablet conectado: {hello.DeviceName} {hello.Width}x{hello.Height} @{hello.DensityDpi}dpi");

        var display = DisplayFinder.FindTarget(settings.DisplayDeviceName)
            ?? throw new InvalidOperationException(
                "Nenhum monitor virtual encontrado. Instale o Virtual Display Driver ou escolha um monitor no menu da bandeja.");

        if (settings.MatchTabletResolution && display.IsVirtual)
        {
            if (DisplayFinder.TrySetResolution(display.DeviceName, hello.Width, hello.Height))
                Log.Write($"{display.DeviceName} ajustado para {hello.Width}x{hello.Height}");
            else if (DisplayFinder.PickClosestMode(display.DeviceName, hello.Width, hello.Height) is { } mode
                     && DisplayFinder.TrySetResolution(display.DeviceName, mode.Width, mode.Height))
                Log.Write($"O driver não oferece {hello.Width}x{hello.Height}; usando {mode.Width}x{mode.Height}. " +
                          "Para a resolução exata, adicione-a em vdd_settings.xml do Virtual Display Driver.");
            else
                Log.Write($"O driver não oferece {hello.Width}x{hello.Height}. Adicione essa resolução em vdd_settings.xml do Virtual Display Driver.");
        }

        using var input = new InputInjector(display.DeviceName, Log.Write);
        _pipeline = new VideoPipeline(display.DeviceName, settings.Fps, settings.BitrateMbps * 1_000_000,
            settings.DrawCursor, settings.AllowHardwareEncoder, Log.Write);
        var videoThread = new Thread(() =>
        {
            try { _pipeline.Run(this, ct); }
            catch (Exception ex) { Log.Write($"Vídeo encerrado: {ex.Message}"); }
            finally { _cts.Cancel(); }
        }) { IsBackground = true, Name = "Video", Priority = ThreadPriority.AboveNormal };
        videoThread.Start();

        var pingTask = PingLoopAsync(ct);
        status($"Conectado: {hello.DeviceName}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await reader.ReadAsync(ct);
                if (frame is null) break;
                switch (frame.Value.Type)
                {
                    case MessageType.Pointer:
                        input.Enqueue(PointerMessage.Parse(frame.Value.Payload));
                        break;
                    case MessageType.RequestKeyframe:
                        _pipeline.RequestKeyframe();
                        break;
                    case MessageType.Pong:
                        var rtt = Stopwatch.GetElapsedTime(PingMessage.Parse(frame.Value.Payload));
                        status($"Conectado: {hello.DeviceName} — {rtt.TotalMilliseconds:F0} ms");
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
        {
            // Disconnected.
        }
        finally
        {
            _cts.Cancel();
            videoThread.Join(3000);
            try { await pingTask; } catch { }
            client.Dispose();
            Log.Write($"Tablet desconectado: {hello.DeviceName}");
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
            await _writer.WriteAsync(MessageType.Ping, PingMessage.Serialize(Stopwatch.GetTimestamp()), ct);
    }

    // IVideoSink — called on the video thread. A failed write ends the session.

    public void OnConfig(int width, int height) => Send(() =>
        _writer.WriteAsync(MessageType.Config, new ConfigMessage(width, height, settings.Fps, settings.BitrateMbps * 1_000_000).Serialize()));

    public void OnCodecConfig(byte[] spsPps) => Send(() => _writer.WriteAsync(MessageType.VideoCsd, spsPps));

    public void OnFrame(EncodedFrame frame)
    {
        VideoFrameMessage.WriteHeader(_frameHeader, frame.PtsUs, frame.Keyframe);
        Send(() => _writer.WriteAsync(MessageType.VideoFrame, _frameHeader, frame.Data));
    }

    private void Send(Func<Task> write)
    {
        try
        {
            write().GetAwaiter().GetResult();
        }
        catch
        {
            _cts.Cancel();
            throw;
        }
    }
}
