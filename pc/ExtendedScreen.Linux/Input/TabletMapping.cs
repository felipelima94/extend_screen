using System.Diagnostics;

namespace ExtendedScreen.Linux.Input;

/// <summary>GNOME tablet settings for the virtual pen (org.gnome.desktop.peripherals.tablet, device 1209:e5c1).</summary>
public static class TabletMapping
{
    private static string SchemaPath =>
        $"org.gnome.desktop.peripherals.tablet:/org/gnome/desktop/peripherals/tablets/{UinputPen.VendorId:x4}:{UinputPen.ProductId:x4}/";

    /// <summary>
    /// Clears any monitor mapping and aspect correction, so the pen spans the whole desktop exactly; <see cref="UinputPen"/>
    /// then places positions inside the tablet's monitor. (Mutter ignores "output" when it names a virtual monitor.)
    /// </summary>
    public static void MapToWholeDesktop(Action<string> log)
    {
        Set("output", "['', '', '']", log);
        Set("keep-aspect", "false", log);
        Set("mapping", "'absolute'", log);
    }

    private static void Set(string key, string value, Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo("gsettings") { UseShellExecute = false, RedirectStandardError = true };
            foreach (var arg in new[] { "set", SchemaPath, key, value }) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) log($"gsettings {key}: {error.Trim()}");
        }
        catch (Exception ex)
        {
            log($"gsettings indisponível ({ex.Message}); a caneta pode cair no lugar errado");
        }
    }
}
