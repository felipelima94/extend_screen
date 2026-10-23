using System.Diagnostics;
using System.Net.Sockets;
using ExtendedScreen.Core.Protocol;
using ExtendedScreen.Server.Platform;

namespace ExtendedScreen.Server.Streaming;

/// <summary>One connected tablet: handshake, video out, input and ping in.</summary>
public sealed class StreamSession(TcpClient client, IScreenBackend backend, AppSettings settings, Action<string> status) : IVideoSink
{
    private readonly CancellationTokenSource _cts = new();
    private FrameWriter _writer = null!;
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

        using var target = backend.Open(hello, settings, Log.Write);
        var videoThread = new Thread(() =>
        {
            try { target.RunVideo(this, ct); }
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
                        target.Enqueue(PointerMessage.Parse(frame.Value.Payload));
                        break;
                    case MessageType.RequestKeyframe:
                        target.RequestKeyframe();
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
