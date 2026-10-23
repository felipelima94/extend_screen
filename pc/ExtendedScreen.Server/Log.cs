namespace ExtendedScreen.Server;

public static class Log
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppSettings.Folder, "log.txt");

    public static event Action<string>? Written;

    public static bool EchoToConsole { get; set; }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take the app down.
            }
            if (EchoToConsole) Console.WriteLine(line);
        }
        Written?.Invoke(message);
    }
}
