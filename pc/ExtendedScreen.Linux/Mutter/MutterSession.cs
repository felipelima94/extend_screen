using Tmds.DBus.Protocol;

namespace ExtendedScreen.Linux.Mutter;

/// <summary>
/// A GNOME (Mutter) remote-desktop + screen-cast session. With no connector it creates a virtual monitor that
/// extends the desktop; with a connector it mirrors that monitor. Pointer input is injected in stream pixels.
/// Mutter tears everything down when <see cref="Dispose"/> runs or this process's D-Bus connection drops.
/// </summary>
public sealed class MutterSession : IDisposable
{
    private const string RdBus = "org.gnome.Mutter.RemoteDesktop";
    private const string ScBus = "org.gnome.Mutter.ScreenCast";
    private const string RdSessionIface = "org.gnome.Mutter.RemoteDesktop.Session";

    public const int BtnLeft = 0x110;

    /// <summary>MessageWriter is a struct: the body must be written through a ref, or the writes land on a copy.</summary>
    private delegate void BodyWriter(ref MessageWriter writer);

    private readonly DBusConnection _connection;
    private readonly string _rdSession;
    private bool _stopped;

    public uint NodeId { get; }
    public string StreamPath { get; }
    public bool IsVirtual { get; }

    private MutterSession(DBusConnection connection, string rdSession, string streamPath, uint nodeId, bool isVirtual)
    {
        _connection = connection;
        _rdSession = rdSession;
        StreamPath = streamPath;
        NodeId = nodeId;
        IsVirtual = isVirtual;
    }

    /// <param name="mirrorConnector">Connector to mirror (e.g. "DP-1"), or null for a new virtual monitor.</param>
    public static async Task<MutterSession> StartAsync(bool drawCursor, string? mirrorConnector, CancellationToken ct)
    {
        var connection = new DBusConnection(DBusAddress.Session!);
        try
        {
            await connection.ConnectAsync();

            var rdSession = await CallAsync(connection, RdBus, "/org/gnome/Mutter/RemoteDesktop", RdBus, "CreateSession", null,
                null, (m, _) => m.GetBodyReader().ReadObjectPathAsString());
            var sessionId = await GetStringPropertyAsync(connection, RdBus, rdSession, RdSessionIface, "SessionId");

            var scSession = await CallAsync(connection, ScBus, "/org/gnome/Mutter/ScreenCast", ScBus, "CreateSession", "a{sv}",
                (ref MessageWriter w) => w.WriteDictionary(new Dictionary<string, VariantValue> { ["remote-desktop-session-id"] = sessionId }),
                (m, _) => m.GetBodyReader().ReadObjectPathAsString());

            var options = new Dictionary<string, VariantValue>
            {
                ["cursor-mode"] = (uint)(drawCursor ? 1 : 0), // 1 = embedded in the frames, 0 = hidden
            };
            string streamPath;
            if (mirrorConnector is null)
            {
                options["is-platform"] = true; // a real extended monitor, not a throwaway window target
                streamPath = await CallAsync(connection, ScBus, scSession, ScBus + ".Session", "RecordVirtual", "a{sv}",
                    (ref MessageWriter w) => w.WriteDictionary(options), (m, _) => m.GetBodyReader().ReadObjectPathAsString());
            }
            else
            {
                streamPath = await CallAsync(connection, ScBus, scSession, ScBus + ".Session", "RecordMonitor", "sa{sv}",
                    (ref MessageWriter w) => { w.WriteString(mirrorConnector); w.WriteDictionary(options); },
                    (m, _) => m.GetBodyReader().ReadObjectPathAsString());
            }

            // Subscribe before Start(): the node id only arrives as a signal.
            var node = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watch = await connection.WatchSignalAsync(ScBus, streamPath, ScBus + ".Stream", "PipeWireStreamAdded",
                (m, _) => m.GetBodyReader().ReadUInt32(),
                n => { if (n.HasValue) node.TrySetResult(n.Value); else if (n.Exception is { } ex) node.TrySetException(ex); },
                ObserverFlags.None, false, null);

            await CallAsync(connection, RdBus, rdSession, RdSessionIface, "Start", null, null, (_, _) => 0);
            var nodeId = await node.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return new MutterSession(connection, rdSession, streamPath, nodeId, mirrorConnector is null);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Returns the ScreenCast interface version, or throws when Mutter's APIs are unreachable.</summary>
    public static async Task<int> GetVersionAsync()
    {
        using var connection = new DBusConnection(DBusAddress.Session!);
        await connection.ConnectAsync();
        var value = await GetPropertyAsync(connection, ScBus, "/org/gnome/Mutter/ScreenCast", ScBus, "Version");
        return value.GetInt32();
    }

    // Input is fire-and-forget: the single connection keeps the order, and waiting for replies would add latency.

    public void PointerMotion(double x, double y)
    {
        var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(RdBus, _rdSession, RdSessionIface, "NotifyPointerMotionAbsolute", "sdd", MessageFlags.NoReplyExpected);
        w.WriteString(StreamPath);
        w.WriteDouble(x);
        w.WriteDouble(y);
        _connection.TrySendMessage(w.CreateMessage());
    }

    public void PointerButton(int button, bool pressed)
    {
        var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(RdBus, _rdSession, RdSessionIface, "NotifyPointerButton", "ib", MessageFlags.NoReplyExpected);
        w.WriteInt32(button);
        w.WriteBool(pressed);
        _connection.TrySendMessage(w.CreateMessage());
    }

    private static async Task<T> CallAsync<T>(DBusConnection connection, string destination, string path, string iface,
        string member, string? signature, BodyWriter? body, MessageValueReader<T> reader)
    {
        // CreateMessage hands the writer's buffer over to the message; disposing the writer afterwards would recycle it.
        var w = connection.GetMessageWriter();
        w.WriteMethodCallHeader(destination, path, iface, member, signature, MessageFlags.None);
        body?.Invoke(ref w);
        var message = w.CreateMessage();
        return await connection.CallMethodAsync(message, reader, null);
    }

    internal static Task<VariantValue> GetPropertyAsync(DBusConnection connection, string destination, string path, string iface, string name) =>
        CallAsync(connection, destination, path, "org.freedesktop.DBus.Properties", "Get", "ss",
            (ref MessageWriter w) => { w.WriteString(iface); w.WriteString(name); },
            (m, _) => m.GetBodyReader().ReadVariantValue());

    private static async Task<string> GetStringPropertyAsync(DBusConnection connection, string destination, string path, string iface, string name) =>
        (await GetPropertyAsync(connection, destination, path, iface, name)).GetString();

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        try
        {
            CallAsync(_connection, RdBus, _rdSession, RdSessionIface, "Stop", null, null, (_, _) => 0)
                .WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch
        {
            // Closing the connection below ends the session anyway.
        }
        _connection.Dispose();
    }
}
