using ExtendedScreen.Core.Protocol;

namespace ExtendedScreen.Server.Platform;

/// <summary>OS-specific side of a session: the screen shown on the tablet and where its input goes.</summary>
public interface IScreenBackend
{
    /// <summary>Prepares the target screen for a tablet that just said HELLO. Throws with a user-facing message on failure.</summary>
    IScreenTarget Open(HelloMessage hello, AppSettings settings, Action<string> log);
}

public interface IScreenTarget : IDisposable
{
    /// <summary>Capture → encode loop; blocks on the video thread until cancelled.</summary>
    void RunVideo(IVideoSink sink, CancellationToken ct);

    void RequestKeyframe();

    /// <summary>Queues a tablet pointer event; must not block the network reader.</summary>
    void Enqueue(PointerMessage msg);
}

public readonly record struct EncodedFrame(byte[] Data, long PtsUs, bool Keyframe);

public interface IVideoSink
{
    /// <summary>Called whenever the encoder is (re)built, before any frame of that size.</summary>
    void OnConfig(int width, int height);

    void OnCodecConfig(byte[] spsPps);

    void OnFrame(EncodedFrame frame);
}
