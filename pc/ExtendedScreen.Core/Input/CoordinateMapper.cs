namespace ExtendedScreen.Core.Input;

public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}

/// <summary>Maps normalized tablet coordinates (0..1) onto the target monitor and the Windows virtual desktop.</summary>
public sealed class CoordinateMapper(ScreenRect monitor, ScreenRect virtualDesktop)
{
    public ScreenRect Monitor { get; } = monitor;
    public ScreenRect VirtualDesktop { get; } = virtualDesktop;

    /// <summary>Pixel position on the virtual desktop, clamped to the monitor.</summary>
    public (int X, int Y) ToPixel(float nx, float ny)
    {
        nx = Math.Clamp(nx, 0f, 1f);
        ny = Math.Clamp(ny, 0f, 1f);
        var x = Monitor.Left + (int)MathF.Round(nx * (Monitor.Width - 1));
        var y = Monitor.Top + (int)MathF.Round(ny * (Monitor.Height - 1));
        return (x, y);
    }

    /// <summary>
    /// Pixel position relative to the top-left of the virtual desktop. InjectSyntheticPointerInput expects this,
    /// not screen coordinates: with a monitor left of/above the primary, screen coordinates land offset.
    /// </summary>
    public (int X, int Y) ToVirtualDesktopPixel(float nx, float ny)
    {
        var (x, y) = ToPixel(nx, ny);
        return (x - VirtualDesktop.Left, y - VirtualDesktop.Top);
    }

    /// <summary>Position in the 0..65535 range that SendInput expects with MOUSEEVENTF_VIRTUALDESK.</summary>
    public (int X, int Y) ToSendInputAbsolute(float nx, float ny)
    {
        var (px, py) = ToPixel(nx, ny);
        var ax = (int)Math.Round((px - VirtualDesktop.Left) * 65535.0 / Math.Max(1, VirtualDesktop.Width - 1));
        var ay = (int)Math.Round((py - VirtualDesktop.Top) * 65535.0 / Math.Max(1, VirtualDesktop.Height - 1));
        return (Math.Clamp(ax, 0, 65535), Math.Clamp(ay, 0, 65535));
    }
}
