using ExtendedScreen.Core.Input;
using ExtendedScreen.Host.Native;

namespace ExtendedScreen.Host.Display;

/// <param name="DeviceName">GDI name, e.g. \\.\DISPLAY3.</param>
/// <param name="AdapterName">Driver description, e.g. "Virtual Display Driver".</param>
public sealed record DisplayInfo(string DeviceName, string AdapterName, ScreenRect Bounds, bool IsPrimary, bool IsVirtual)
{
    public override string ToString() =>
        $"{DeviceName.Replace(@"\\.\", "")} — {AdapterName} ({Bounds.Width}x{Bounds.Height}){(IsPrimary ? " [principal]" : "")}";
}

public static class DisplayFinder
{
    /// <summary>Adapter names used by IddCx virtual display drivers (VirtualDrivers/Virtual-Display-Driver and its forks).</summary>
    private static readonly string[] VirtualAdapterHints = ["Virtual Display", "IddSample", "MttVDD", "Indirect Display"];

    public static List<DisplayInfo> GetActiveDisplays()
    {
        var result = new List<DisplayInfo>();
        var dd = User32.DISPLAY_DEVICE.Create();
        for (var i = 0; User32.EnumDisplayDevices(null, i, ref dd, 0); i++, dd = User32.DISPLAY_DEVICE.Create())
        {
            if ((dd.StateFlags & User32.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            var mode = User32.DEVMODE.Create();
            if (!User32.EnumDisplaySettings(dd.DeviceName, User32.ENUM_CURRENT_SETTINGS, ref mode)) continue;

            var bounds = new ScreenRect(mode.dmPositionX, mode.dmPositionY, mode.dmPelsWidth, mode.dmPelsHeight);
            var isVirtual = VirtualAdapterHints.Any(h => dd.DeviceString.Contains(h, StringComparison.OrdinalIgnoreCase));
            result.Add(new DisplayInfo(dd.DeviceName, dd.DeviceString, bounds,
                (dd.StateFlags & User32.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0, isVirtual));
        }
        return result;
    }

    /// <summary>The preferred display: the one named in settings, else the first virtual one.</summary>
    public static DisplayInfo? FindTarget(string? preferredDeviceName)
    {
        var displays = GetActiveDisplays();
        if (!string.IsNullOrEmpty(preferredDeviceName))
        {
            var match = displays.FirstOrDefault(d => d.DeviceName.Equals(preferredDeviceName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return displays.FirstOrDefault(d => d.IsVirtual);
    }

    public static ScreenRect GetVirtualDesktop() => new(
        User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN),
        User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN),
        User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN),
        User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN));

    public static List<(int Width, int Height)> GetSupportedModes(string deviceName)
    {
        var modes = new HashSet<(int, int)>();
        var mode = User32.DEVMODE.Create();
        for (var i = 0; User32.EnumDisplaySettings(deviceName, i, ref mode); i++)
            modes.Add((mode.dmPelsWidth, mode.dmPelsHeight));
        return [.. modes];
    }

    /// <summary>
    /// The mode closest to the tablet: same aspect ratio as near as possible, then the largest that is not bigger
    /// than the tablet (bigger only when nothing smaller exists).
    /// </summary>
    public static (int Width, int Height)? PickClosestMode(string deviceName, int width, int height)
    {
        var target = (double)width / height;
        return GetSupportedModes(deviceName)
            .OrderBy(m => Math.Round(Math.Abs((double)m.Width / m.Height - target), 2))
            .ThenBy(m => m.Width > width || m.Height > height ? 1 : 0)
            .ThenByDescending(m => (long)m.Width * m.Height)
            .Select(m => ((int, int)?)m)
            .FirstOrDefault();
    }

    /// <summary>Switches the display to the given resolution if the driver exposes it.</summary>
    /// <returns>True when the display is now at that resolution.</returns>
    public static bool TrySetResolution(string deviceName, int width, int height)
    {
        var current = User32.DEVMODE.Create();
        if (User32.EnumDisplaySettings(deviceName, User32.ENUM_CURRENT_SETTINGS, ref current)
            && current.dmPelsWidth == width && current.dmPelsHeight == height)
            return true;

        var mode = User32.DEVMODE.Create();
        for (var i = 0; User32.EnumDisplaySettings(deviceName, i, ref mode); i++)
        {
            if (mode.dmPelsWidth != width || mode.dmPelsHeight != height) continue;
            var target = User32.DEVMODE.Create();
            target.dmPelsWidth = width;
            target.dmPelsHeight = height;
            target.dmFields = User32.DM_PELSWIDTH | User32.DM_PELSHEIGHT;
            return User32.ChangeDisplaySettingsEx(deviceName, ref target, IntPtr.Zero, User32.CDS_UPDATEREGISTRY, IntPtr.Zero)
                   == User32.DISP_CHANGE_SUCCESSFUL;
        }
        return false;
    }
}
