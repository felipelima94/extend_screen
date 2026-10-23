using System.Diagnostics;

namespace ExtendedScreen.Server.Adb;

/// <summary>
/// Watches for Android devices over USB and keeps an <c>adb reverse</c> tunnel open on each one,
/// so the tablet app can reach the PC at localhost:port.
/// </summary>
public sealed class AdbManager(int port) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _tunneled = [];
    private Task? _loop;

    public string? AdbPath { get; private set; }

    /// <summary>Raised with a human-readable status whenever the device list changes.</summary>
    public event Action<string>? StatusChanged;

    public IReadOnlyCollection<string> ConnectedDevices
    {
        get { lock (_tunneled) return [.. _tunneled]; }
    }

    public void Start()
    {
        AdbPath = FindAdb();
        if (AdbPath is null)
        {
            StatusChanged?.Invoke(OperatingSystem.IsWindows()
                ? "adb.exe não encontrado. Rode scripts\\get-adb.ps1."
                : "adb não encontrado. Instale com: sudo dnf install android-tools");
            return;
        }
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        await RunAdbAsync("start-server", ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusChanged?.Invoke($"Erro no adb: {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ContinueWith(_ => { });
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var (_, output) = await RunAdbAsync("devices", ct);
        var devices = new Dictionary<string, string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))
        {
            var parts = line.Split('\t');
            if (parts.Length >= 2) devices[parts[0]] = parts[1];
        }

        var changed = false;
        string? unauthorized = null;
        foreach (var (serial, state) in devices)
        {
            if (state == "unauthorized") unauthorized = serial;
            if (state != "device") continue;
            lock (_tunneled) if (_tunneled.Contains(serial)) continue;

            var (code, msg) = await RunAdbAsync($"-s {serial} reverse tcp:{port} tcp:{port}", ct);
            if (code == 0)
            {
                lock (_tunneled) _tunneled.Add(serial);
                changed = true;
            }
            else
            {
                Report($"Falha no adb reverse em {serial}: {msg.Trim()}");
            }
        }

        lock (_tunneled)
        {
            changed |= _tunneled.RemoveWhere(s => !devices.TryGetValue(s, out var st) || st != "device") > 0;
        }

        var tunneled = ConnectedDevices;
        if (tunneled.Count > 0) Report($"USB: {string.Join(", ", tunneled)}");
        else if (unauthorized is not null) Report("Autorize a depuração USB na tela do tablet");
        else if (changed || _lastStatus is null) Report("Nenhum tablet conectado via USB");
    }

    private string? _lastStatus;

    /// <summary>Raises <see cref="StatusChanged"/> only when the text changes (this runs every 2 s).</summary>
    private void Report(string status)
    {
        if (status == _lastStatus) return;
        _lastStatus = status;
        StatusChanged?.Invoke(status);
    }

    private async Task<(int ExitCode, string Output)> RunAdbAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(AdbPath!, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await stdout + await stderr);
    }

    /// <summary>Looks for tools/platform-tools/adb next to or above the executable, then in the Android SDK, then on PATH.</summary>
    public static string? FindAdb()
    {
        var exe = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var candidate in new[]
            {
                Path.Combine(dir.FullName, "platform-tools", exe),
                Path.Combine(dir.FullName, "tools", "platform-tools", exe),
            })
            {
                if (File.Exists(candidate)) return candidate;
            }
        }

        var sdk = Environment.GetEnvironmentVariable("ANDROID_HOME")
                  ?? (OperatingSystem.IsWindows()
                      ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk")
                      : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Android", "Sdk"));
        var sdkAdb = Path.Combine(sdk, "platform-tools", exe);
        if (File.Exists(sdkAdb)) return sdkAdb;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir.Trim(), exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
