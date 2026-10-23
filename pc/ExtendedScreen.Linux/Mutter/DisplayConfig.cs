using ExtendedScreen.Core.Input;
using Tmds.DBus.Protocol;

namespace ExtendedScreen.Linux.Mutter;

/// <summary>A physical or virtual monitor as Mutter describes it.</summary>
/// <param name="Layout">Where the monitor sits on the desktop, in the coordinates input devices use; null when it is off.</param>
public sealed record MonitorInfo(string Connector, string Vendor, string Product, string Serial, int Width, int Height, bool IsActive,
    ScreenRect? Layout = null)
{
    public bool IsVirtual => Connector.StartsWith("Meta-", StringComparison.Ordinal);

    public override string ToString() =>
        $"{Connector}\t{Vendor} {Product}\t{(IsActive ? $"{Width}x{Height}" : "desligado")}{(Layout is { } l ? $" em {l.Left},{l.Top}" : "")}{(IsVirtual ? "\tvirtual" : "")}";
}

/// <summary>Reads the monitor list and desktop layout from org.gnome.Mutter.DisplayConfig.GetCurrentState.</summary>
public static class DisplayConfig
{
    private const uint LayoutModePhysical = 2;

    /// <summary>Bounding box of every active monitor: the area an unmapped tablet covers.</summary>
    public static ScreenRect? GetDesktop(IEnumerable<MonitorInfo> monitors)
    {
        var rects = monitors.Where(m => m.Layout is not null).Select(m => m.Layout!.Value).ToList();
        if (rects.Count == 0) return null;
        var left = rects.Min(r => r.Left);
        var top = rects.Min(r => r.Top);
        return new ScreenRect(left, top, rects.Max(r => r.Right) - left, rects.Max(r => r.Bottom) - top);
    }

    public static async Task<List<MonitorInfo>> GetMonitorsAsync()
    {
        using var connection = new DBusConnection(DBusAddress.Session!);
        await connection.ConnectAsync();

        var w = connection.GetMessageWriter();
        w.WriteMethodCallHeader("org.gnome.Mutter.DisplayConfig", "/org/gnome/Mutter/DisplayConfig",
            "org.gnome.Mutter.DisplayConfig", "GetCurrentState", null, MessageFlags.None);
        var message = w.CreateMessage();

        // (u serial, a((ssss) a(siiddada{sv}) a{sv}) monitors, a(iiduba(ssss)a{sv}) logical, a{sv} props)
        return await connection.CallMethodAsync(message, (m, _) =>
        {
            var r = m.GetBodyReader();
            r.ReadUInt32();
            var result = new List<MonitorInfo>();
            var monitorsEnd = r.ReadArrayStart(DBusType.Struct);
            while (r.HasNext(monitorsEnd))
            {
                r.AlignStruct();
                r.AlignStruct();
                var connector = r.ReadString();
                var vendor = r.ReadString();
                var product = r.ReadString();
                var serial = r.ReadString();

                int width = 0, height = 0;
                var active = false;
                var modesEnd = r.ReadArrayStart(DBusType.Struct);
                while (r.HasNext(modesEnd))
                {
                    r.AlignStruct();
                    r.ReadString(); // mode id
                    var mw = r.ReadInt32();
                    var mh = r.ReadInt32();
                    r.ReadDouble(); // refresh rate
                    r.ReadDouble(); // preferred scale
                    var scalesEnd = r.ReadArrayStart(DBusType.Double);
                    r.SkipTo(scalesEnd);
                    var props = r.ReadDictionaryOfStringToVariantValue();
                    if (props.TryGetValue("is-current", out var current) && current.GetBool())
                    {
                        (width, height, active) = (mw, mh, true);
                    }
                }
                r.ReadDictionaryOfStringToVariantValue();
                result.Add(new MonitorInfo(connector, vendor, product, serial, width, height, active));
            }

            var logical = new List<(int X, int Y, double Scale, uint Transform, List<string> Connectors)>();
            var logicalEnd = r.ReadArrayStart(DBusType.Struct);
            while (r.HasNext(logicalEnd))
            {
                r.AlignStruct();
                var x = r.ReadInt32();
                var y = r.ReadInt32();
                var scale = r.ReadDouble();
                var transform = r.ReadUInt32();
                r.ReadBool(); // primary
                var connectors = new List<string>();
                var specsEnd = r.ReadArrayStart(DBusType.Struct);
                while (r.HasNext(specsEnd))
                {
                    r.AlignStruct();
                    connectors.Add(r.ReadString());
                    r.ReadString();
                    r.ReadString();
                    r.ReadString();
                }
                r.ReadDictionaryOfStringToVariantValue();
                logical.Add((x, y, scale, transform, connectors));
            }
            var stateProps = r.ReadDictionaryOfStringToVariantValue();
            var physicalLayout = stateProps.TryGetValue("layout-mode", out var mode) && mode.GetUInt32() == LayoutModePhysical;

            for (var i = 0; i < result.Count; i++)
            {
                var mon = result[i];
                var lm = logical.FirstOrDefault(l => l.Connectors.Contains(mon.Connector));
                if (!mon.IsActive || lm.Connectors is null) continue;
                // Odd transforms are 90°/270° rotations; logical layout divides by the scale.
                var (w, h) = lm.Transform % 2 == 1 ? (mon.Height, mon.Width) : (mon.Width, mon.Height);
                var divisor = physicalLayout ? 1.0 : lm.Scale;
                result[i] = mon with { Layout = new ScreenRect(lm.X, lm.Y, (int)Math.Round(w / divisor), (int)Math.Round(h / divisor)) };
            }
            return result;
        }, null);
    }
}
