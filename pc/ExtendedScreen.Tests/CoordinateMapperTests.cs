using ExtendedScreen.Core.Input;

namespace ExtendedScreen.Tests;

public class CoordinateMapperTests
{
    // Primary 1920x1080 at (0,0); tablet monitor 2560x1600 to the right.
    private static readonly ScreenRect Tablet = new(1920, 0, 2560, 1600);
    private static readonly ScreenRect Desktop = new(0, 0, 4480, 1600);

    [Fact]
    public void ToPixel_MapsCornersAndCenter()
    {
        var m = new CoordinateMapper(Tablet, Desktop);
        Assert.Equal((1920, 0), m.ToPixel(0, 0));
        Assert.Equal((4479, 1599), m.ToPixel(1, 1));
        Assert.Equal((3200, 800), m.ToPixel(0.5f, 0.5f));
    }

    [Fact]
    public void ToPixel_ClampsOutOfRange()
    {
        var m = new CoordinateMapper(Tablet, Desktop);
        Assert.Equal((1920, 0), m.ToPixel(-0.3f, -2f));
        Assert.Equal((4479, 1599), m.ToPixel(1.7f, 3f));
    }

    [Fact]
    public void ToSendInputAbsolute_SpansVirtualDesktop()
    {
        var m = new CoordinateMapper(Tablet, Desktop);
        var (x0, y0) = m.ToSendInputAbsolute(0, 0);
        Assert.Equal((int)Math.Round(1920 * 65535.0 / 4479), x0);
        Assert.Equal(0, y0);
        Assert.Equal((65535, 65535), m.ToSendInputAbsolute(1, 1));
    }

    [Fact]
    public void ToPixel_MonitorWithNegativeOrigin()
    {
        // Tablet placed to the left of the primary monitor.
        var m = new CoordinateMapper(new ScreenRect(-1280, 100, 1280, 800), new ScreenRect(-1280, 0, 3200, 1080));
        Assert.Equal((-1280, 100), m.ToPixel(0, 0));
        Assert.Equal((-1, 899), m.ToPixel(1, 1));
    }

    [Fact]
    public void ToVirtualDesktopPixel_IsRelativeToDesktopOrigin()
    {
        // Portrait monitor at (-1080,-588) makes the virtual desktop start there.
        var m = new CoordinateMapper(new ScreenRect(3440, 0, 2560, 1440), new ScreenRect(-1080, -588, 7080, 2560));
        Assert.Equal((3440 + 1080, 588), m.ToVirtualDesktopPixel(0, 0));
        Assert.Equal((5999 + 1080, 1439 + 588), m.ToVirtualDesktopPixel(1, 1));
    }
}
