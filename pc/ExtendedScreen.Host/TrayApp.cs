using System.Diagnostics;
using System.Drawing.Drawing2D;
using ExtendedScreen.Core.Protocol;
using ExtendedScreen.Host.Adb;
using ExtendedScreen.Host.Display;
using ExtendedScreen.Host.Streaming;

namespace ExtendedScreen.Host;

public sealed class TrayApp : ApplicationContext
{
    private static readonly int[] FpsOptions = [30, 60];
    private static readonly int[] BitrateOptions = [8, 15, 20, 30, 50];

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem = new("Iniciando…") { Enabled = false };
    private readonly ToolStripMenuItem _usbItem = new("USB: —") { Enabled = false };
    private readonly SynchronizationContext _ui;
    private readonly AdbManager _adb = new(ProtocolConstants.Port);
    private readonly StreamServer _server;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "ExtendedScreen",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        BuildMenu();

        _adb.StatusChanged += s => OnUi(() =>
        {
            _usbItem.Text = Truncate(s);
            Log.Write(s);
        });
        _server = new StreamServer(ProtocolConstants.Port, () => _settings, s => OnUi(() => SetStatus(s)));

        try
        {
            _server.Start();
        }
        catch (Exception ex)
        {
            SetStatus($"Porta {ProtocolConstants.Port} ocupada: {ex.Message}");
        }
        _adb.Start();

        if (DisplayFinder.FindTarget(_settings.DisplayDeviceName) is null)
        {
            _icon.ShowBalloonTip(8000, "ExtendedScreen",
                "Nenhum monitor virtual encontrado. Instale o Virtual Display Driver (veja o README) ou escolha um monitor no menu.",
                ToolTipIcon.Warning);
        }
    }

    private void SetStatus(string status)
    {
        _statusItem.Text = Truncate(status);
        _icon.Text = Truncate("ExtendedScreen — " + status, 63);
    }

    private static string Truncate(string s, int max = 80) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private void OnUi(Action action) => _ui.Post(_ => action(), null);

    private void BuildMenu()
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_usbItem);
        menu.Items.Add(new ToolStripSeparator());

        var monitors = new ToolStripMenuItem("Monitor");
        var auto = new ToolStripMenuItem("Automático (primeiro monitor virtual)") { Checked = _settings.DisplayDeviceName is null };
        auto.Click += (_, _) => Apply(() => _settings.DisplayDeviceName = null);
        monitors.DropDownItems.Add(auto);
        foreach (var d in DisplayFinder.GetActiveDisplays())
        {
            var item = new ToolStripMenuItem(d + (d.IsVirtual ? " [virtual]" : ""))
            {
                Checked = string.Equals(_settings.DisplayDeviceName, d.DeviceName, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => Apply(() => _settings.DisplayDeviceName = d.DeviceName);
            monitors.DropDownItems.Add(item);
        }
        menu.Items.Add(monitors);

        var fps = new ToolStripMenuItem("FPS");
        foreach (var f in FpsOptions)
        {
            var item = new ToolStripMenuItem($"{f} fps") { Checked = _settings.Fps == f };
            item.Click += (_, _) => Apply(() => _settings.Fps = f);
            fps.DropDownItems.Add(item);
        }
        menu.Items.Add(fps);

        var bitrate = new ToolStripMenuItem("Qualidade (bitrate)");
        foreach (var b in BitrateOptions)
        {
            var item = new ToolStripMenuItem($"{b} Mbps") { Checked = _settings.BitrateMbps == b };
            item.Click += (_, _) => Apply(() => _settings.BitrateMbps = b);
            bitrate.DropDownItems.Add(item);
        }
        menu.Items.Add(bitrate);

        AddToggle(menu, "Ajustar resolução do monitor virtual ao tablet", () => _settings.MatchTabletResolution, v => _settings.MatchTabletResolution = v);
        AddToggle(menu, "Mostrar cursor do mouse", () => _settings.DrawCursor, v => _settings.DrawCursor = v);
        AddToggle(menu, "Usar encoder de hardware (GPU)", () => _settings.AllowHardwareEncoder, v => _settings.AllowHardwareEncoder = v);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Abrir log", null, (_, _) => OpenFile(Log.FilePath));
        menu.Items.Add("Abrir pasta de configurações", null, (_, _) => OpenFile(AppSettings.Folder));
        menu.Items.Add("Sair", null, (_, _) => ExitThread());
    }

    private void AddToggle(ContextMenuStrip menu, string text, Func<bool> get, Action<bool> set)
    {
        var item = new ToolStripMenuItem(text) { Checked = get() };
        item.Click += (_, _) => Apply(() => set(!get()));
        menu.Items.Add(item);
    }

    /// <summary>Saves the change and restarts the stream so it takes effect.</summary>
    private void Apply(Action change)
    {
        change();
        _settings.Save();
        _ = _server.RestartSessionAsync();
    }

    private static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) Directory.CreateDirectory(AppSettings.Folder);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ExtendedScreen");
        }
    }

    /// <summary>Draws a small tablet glyph so no .ico file is needed.</summary>
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.FromArgb(40, 120, 220));
            using var screen = new SolidBrush(Color.White);
            g.FillRectangle(body, 2, 6, 28, 20);
            g.FillRectangle(screen, 5, 9, 22, 14);
            g.FillRectangle(body, 13, 26, 6, 3);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _server.Dispose();
        _adb.Dispose();
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
