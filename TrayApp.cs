using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace SerialBridge;

/// <summary>
/// Icono de Lector USB en la barra de tareas (SerialBridge.exe --tray). Un servicio de Windows no puede
/// mostrar interfaz, así que este proceso corre en la sesión de cada usuario y habla con el servicio por HTTP.
/// </summary>
internal static class TrayApp
{
    public static int Run()
    {
        // Un solo icono por sesión de usuario.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\SerialBridge.Tray", out var isFirstInstance);
        if (!isFirstInstance)
            return 0;

        // Las instrucciones de nivel superior corren en MTA; WinForms necesita un hilo STA.
        var ui = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext());
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        return 0;
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private const string AppName = "Lector USB";
    private const string ServiceName = "SerialBridge";
    private static readonly TimeSpan RestartTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _restarting;

    public TrayContext()
    {
        _statusItem = new ToolStripMenuItem("Consultando…") { Enabled = false };
        var configItem = new ToolStripMenuItem("Configuración", null, (_, _) => OpenConfiguration());
        configItem.Font = new System.Drawing.Font(configItem.Font, System.Drawing.FontStyle.Bold);
        _restartItem = new ToolStripMenuItem("Reiniciar", null, async (_, _) => await RestartAsync());
        var uninstallItem = new ToolStripMenuItem("Desinstalar", null, (_, _) => Uninstall());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([_statusItem, new ToolStripSeparator(), configItem, _restartItem, new ToolStripSeparator(), uninstallItem]);
        menu.Opening += async (_, _) => await RefreshStatusAsync();

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = AppName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenConfiguration();
        };

        _timer = new System.Windows.Forms.Timer { Interval = 5000 };
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        _timer.Start();
        _ = RefreshStatusAsync();
    }

    /// <summary>El puerto se lee en cada uso: puede cambiarse desde la página mientras el icono está abierto.</summary>
    private static string BaseUrl
    {
        get
        {
            var port = new BridgeSettings().WebSocketPort;
            try
            {
                var saved = JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(SettingsStore.DefaultFilePath), Json.File);
                if (saved is { WebSocketPort: > 0 and <= 65535 })
                    port = saved.WebSocketPort;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            return $"http://127.0.0.1:{port}/";
        }
    }

    private static void OpenConfiguration() =>
        Process.Start(new ProcessStartInfo(BaseUrl) { UseShellExecute = true });

    private async Task RefreshStatusAsync()
    {
        string text;
        try
        {
            using var status = JsonDocument.Parse(await _http.GetStringAsync(BaseUrl + "api/status"));
            text = status.RootElement.GetProperty("serial").GetProperty("message").GetString() ?? "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            text = _restarting ? "Reiniciando el servicio…" : "El servicio no responde";
        }

        _statusItem.Text = text;
        var tooltip = AppName + ": " + text;
        _icon.Text = tooltip.Length > 127 ? tooltip[..126] + "…" : tooltip;
    }

    private async Task RestartAsync()
    {
        if (_restarting)
            return;
        _restarting = true;
        _restartItem.Enabled = false;
        try
        {
            if (!await RequestRestartAsync() && !RestartServiceElevated())
                return;

            ShowBalloon("Reiniciando el servicio…", ToolTipIcon.Info);
            var ok = await WaitUntilRespondingAsync();
            ShowBalloon(ok ? "Servicio reiniciado." : "El servicio no volvió a responder. Revise el Visor de eventos.",
                ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        finally
        {
            _restarting = false;
            _restartItem.Enabled = true;
            await RefreshStatusAsync();
        }
    }

    /// <summary>Pide al servicio que se reinicie (no requiere permisos de administrador).</summary>
    private async Task<bool> RequestRestartAsync()
    {
        try
        {
            using var body = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(BaseUrl + "api/service/restart", body);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Si el servicio está detenido o no responde, hay que iniciarlo con el Administrador de servicios,
    /// lo que exige permisos de administrador: Windows mostrará su aviso de confirmación.
    /// </summary>
    private bool RestartServiceElevated()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c net stop {ServiceName} & net start {ServiceName}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            process?.WaitForExit(60_000);
            return true;
        }
        catch (Win32Exception)
        {
            ShowBalloon("No se reinició el servicio: se canceló el permiso de administrador.", ToolTipIcon.Warning);
            return false;
        }
    }

    private async Task<bool> WaitUntilRespondingAsync()
    {
        // El servicio tarda ~0,5 s en cerrarse: se espera antes de consultar para no hablar con el proceso viejo.
        await Task.Delay(1500);
        var deadline = DateTime.UtcNow + RestartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await _http.GetAsync(BaseUrl + "api/status");
                if (response.StatusCode == HttpStatusCode.OK)
                    return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            await Task.Delay(1000);
        }
        return false;
    }

    /// <summary>
    /// Abre el desinstalador que deja el instalador junto al .exe (unins000.exe). Él mismo pide
    /// confirmación y permisos de administrador, detiene el servicio y cierra este icono.
    /// </summary>
    private void Uninstall()
    {
        var uninstaller = Directory.EnumerateFiles(AppContext.BaseDirectory, "unins*.exe").Order().FirstOrDefault();
        try
        {
            if (uninstaller is not null)
                Process.Start(new ProcessStartInfo(uninstaller) { UseShellExecute = true });
            else
                // Sin desinstalador (instalación manual o desarrollo): se abre la lista de aplicaciones de Windows.
                Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // El usuario canceló el aviso de permisos de administrador: no hay nada que hacer.
        }
    }

    private void ShowBalloon(string text, ToolTipIcon icon) =>
        _icon.ShowBalloonTip(3000, AppName, text, icon);

    private static System.Drawing.Icon LoadIcon()
    {
        using var stream = typeof(TrayContext).Assembly.GetManifestResourceStream("SerialBridge.scanner.ico");
        return stream is null
            ? System.Drawing.SystemIcons.Application
            : new System.Drawing.Icon(stream, SystemInformation.SmallIconSize);
    }

    protected override void ExitThreadCore()
    {
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _http.Dispose();
        base.ExitThreadCore();
    }
}
