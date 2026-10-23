using System.Net;
using System.Net.Sockets;

namespace ExtendedScreen.Host.Streaming;

/// <summary>Listens on localhost (reached from the tablet through adb reverse). One tablet at a time.</summary>
public sealed class StreamServer(int port, Func<AppSettings> settings, Action<string> status) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private StreamSession? _current;
    private Task? _currentTask;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
        status("Aguardando o tablet…");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                Log.Write($"Erro ao aceitar conexão: {ex.Message}");
                continue;
            }

            // A new connection replaces the old one (e.g. the app was reopened).
            await StopCurrentAsync();
            var session = new StreamSession(client, settings(), status);
            _current = session;
            _currentTask = Task.Run(async () =>
            {
                try
                {
                    await session.RunAsync(ct);
                }
                catch (Exception ex)
                {
                    Log.Write($"Sessão encerrada: {ex.Message}");
                    status($"Erro: {ex.Message}");
                    client.Dispose();
                    return;
                }
                if (ReferenceEquals(_current, session)) status("Aguardando o tablet…");
            }, ct);
        }
    }

    /// <summary>Ends the current session; the tablet reconnects by itself and picks up new settings.</summary>
    public Task RestartSessionAsync() => StopCurrentAsync();

    private async Task StopCurrentAsync()
    {
        _current?.Stop();
        if (_currentTask is not null)
        {
            try { await _currentTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        _current = null;
        _currentTask = null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _current?.Stop();
        _listener?.Stop();
        try { _currentTask?.Wait(3000); } catch { }
    }
}
