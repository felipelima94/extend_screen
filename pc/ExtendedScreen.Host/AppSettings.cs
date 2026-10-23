using System.Text.Json;

namespace ExtendedScreen.Host;

public sealed class AppSettings
{
    /// <summary>GDI name of the monitor to stream (e.g. \\.\DISPLAY3); null picks the first virtual display.</summary>
    public string? DisplayDeviceName { get; set; }

    public int Fps { get; set; } = 60;
    public int BitrateMbps { get; set; } = 20;

    /// <summary>Switch the virtual monitor to the tablet's native resolution when it connects.</summary>
    public bool MatchTabletResolution { get; set; } = true;

    public bool DrawCursor { get; set; } = true;
    public bool AllowHardwareEncoder { get; set; } = true;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExtendedScreen");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch
        {
            // Corrupt file: fall back to defaults.
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}
