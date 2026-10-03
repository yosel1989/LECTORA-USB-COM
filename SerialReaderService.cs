using System.IO.Ports;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SerialBridge;

public sealed record SerialStatus(
    bool Connected,
    string? Port,
    string Message,
    string? LastFrame,
    DateTimeOffset? LastFrameAt,
    long FrameCount);

/// <summary>
/// Abre el puerto COM configurado, arma tramas (separadas por CR/LF o por silencio)
/// y las reenvía al <see cref="WebSocketHub"/>. Si la lectora se desconecta, reintenta sola.
/// </summary>
public sealed class SerialReaderService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(2);
    private const int MaxFrameLength = 64 * 1024;

    private readonly SettingsStore _store;
    private readonly WebSocketHub _hub;
    private readonly ILogger<SerialReaderService> _logger;
    private readonly object _statusLock = new();
    private readonly StringBuilder _buffer = new();
    private readonly Timer _idleTimer;
    private TaskCompletionSource<string> _sessionEnd = NewSignal();
    private volatile string? _activePort;
    private SerialStatus _status = new(false, null, "Iniciando", null, null, 0);

    public SerialReaderService(SettingsStore store, WebSocketHub hub, ILogger<SerialReaderService> logger)
    {
        _store = store;
        _hub = hub;
        _logger = logger;
        _idleTimer = new Timer(_ => FlushIdleBuffer());
    }

    public SerialStatus Status
    {
        get { lock (_statusLock) return _status; }
    }

    public string BuildStatusMessage()
    {
        var s = Status;
        return JsonSerializer.Serialize(new { type = "status", connected = s.Connected, port = s.Port, message = s.Message }, Json.Wire);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _store.Changed += OnSettingsChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Se crea antes de leer la configuración para no perder un cambio intermedio.
                Volatile.Write(ref _sessionEnd, NewSignal());
                try
                {
                    await RunSessionAsync(_store.Current, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Error inesperado en la lectura serie");
                    SetStatus(false, null, $"Error: {ex.Message}");
                    await WaitAsync(RetryDelay, stoppingToken);
                }
            }
        }
        finally
        {
            _store.Changed -= OnSettingsChanged;
        }
    }

    private async Task RunSessionAsync(BridgeSettings settings, CancellationToken ct)
    {
        if (!settings.HasComPort)
        {
            SetStatus(false, null, "Seleccione en la configuración la lectora USB (puerto COM)");
            await WaitAsync(RetryDelay, ct);
            return;
        }

        var available = PortNames.Available();
        var portName = settings.ComPort;

        using var port = new SerialPort(portName, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits)
        {
            Encoding = Encoding.UTF8,
            // Muchos dispositivos USB CDC no transmiten hasta que el host activa DTR/RTS.
            DtrEnable = true,
            RtsEnable = true,
        };

        try
        {
            port.Open();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            var reason = ex switch
            {
                UnauthorizedAccessException => $"{portName} está en uso por otro programa",
                FileNotFoundException => $"{portName} no está conectado (esperando la lectora)",
                _ => $"No se pudo abrir {portName}: {ex.Message}",
            };
            if (SetStatus(false, portName, reason))
                _logger.LogWarning("{Reason}. Reintentando cada {Seconds}s", reason, RetryDelay.TotalSeconds);
            await WaitAsync(RetryDelay, ct);
            return;
        }

        _activePort = portName;
        port.DataReceived += OnDataReceived;
        SetStatus(true, portName, $"Conectado a {portName} ({settings.BaudRate} baudios)");
        _logger.LogInformation("Leyendo {Port} a {Baud} baudios", portName, settings.BaudRate);

        try
        {
            var reason = await MonitorAsync(port, available.Contains(portName, StringComparer.OrdinalIgnoreCase), ct);
            SetStatus(false, portName, reason);
            _logger.LogInformation("Cerrando {Port}: {Reason}", portName, reason);
        }
        finally
        {
            port.DataReceived -= OnDataReceived;
            _activePort = null;
            DiscardBuffer();
            try { port.Close(); } catch (IOException) { /* el dispositivo ya no existe */ }
        }
    }

    /// <summary>Espera a que cambie la configuración, falle la lectura o se desenchufe la lectora.</summary>
    private async Task<string> MonitorAsync(SerialPort port, bool listedInSystem, CancellationToken ct)
    {
        var sessionEnd = Volatile.Read(ref _sessionEnd).Task;
        while (!ct.IsCancellationRequested)
        {
            if (await Task.WhenAny(sessionEnd, Task.Delay(HealthCheckInterval, ct)) == sessionEnd)
                return sessionEnd.Result;

            var stillPresent = !listedInSystem
                || PortNames.Available().Contains(port.PortName, StringComparer.OrdinalIgnoreCase);
            if (!port.IsOpen || !stillPresent)
                return $"{port.PortName} desconectado";
        }
        return "Servicio detenido";
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        var port = (SerialPort)sender;

        // DataReceived puede dispararse en paralelo en varios hilos: leer y publicar
        // dentro del mismo bloqueo mantiene el orden de las tramas.
        lock (_buffer)
        {
            string chunk;
            try
            {
                chunk = port.ReadExisting();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
            {
                Volatile.Read(ref _sessionEnd).TrySetResult($"Error de lectura: {ex.Message}");
                return;
            }

            foreach (var c in chunk)
            {
                if (c is '\r' or '\n')
                    PublishBuffered(port.PortName);
                else
                    _buffer.Append(c);
            }

            if (_buffer.Length >= MaxFrameLength)
                PublishBuffered(port.PortName);

            var idle = _store.Current.FrameIdleTimeoutMs;
            _idleTimer.Change(_buffer.Length > 0 && idle > 0 ? idle : Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>Para lectoras que no envían terminador: lo acumulado se emite tras el silencio configurado.</summary>
    private void FlushIdleBuffer()
    {
        var portName = _activePort;
        if (portName is null)
            return;
        lock (_buffer)
            PublishBuffered(portName);
    }

    private void DiscardBuffer()
    {
        lock (_buffer)
        {
            _buffer.Clear();
            _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>Debe llamarse con el bloqueo de _buffer tomado.</summary>
    private void PublishBuffered(string portName)
    {
        if (_buffer.Length == 0)
            return;

        var frame = _buffer.ToString();
        _buffer.Clear();

        var now = DateTimeOffset.Now;
        lock (_statusLock)
            _status = _status with { LastFrame = frame, LastFrameAt = now, FrameCount = _status.FrameCount + 1 };
        _logger.LogDebug("Trama de {Port}: {Frame}", portName, frame);

        _hub.Broadcast(_store.Current.IsRawOutput
            ? frame
            : JsonSerializer.Serialize(new { type = "frame", port = portName, data = frame, timestamp = now }, Json.Wire));
    }

    /// <summary>Devuelve true si el estado cambió (sirve para no repetir el mismo aviso en cada reintento).</summary>
    private bool SetStatus(bool connected, string? port, string message)
    {
        lock (_statusLock)
        {
            if (_status.Connected == connected && _status.Port == port && _status.Message == message)
                return false;
            _status = _status with { Connected = connected, Port = port, Message = message };
        }

        if (!_store.Current.IsRawOutput)
            _hub.Broadcast(BuildStatusMessage());
        return true;
    }

    private void OnSettingsChanged(BridgeSettings previous, BridgeSettings current)
    {
        var serialChanged = previous.ComPort != current.ComPort
            || previous.BaudRate != current.BaudRate
            || previous.DataBits != current.DataBits
            || previous.Parity != current.Parity
            || previous.StopBits != current.StopBits;
        if (serialChanged)
            Volatile.Read(ref _sessionEnd).TrySetResult("Configuración serie actualizada");
    }

    /// <summary>Espera el retardo, pero despierta antes si cambia la configuración.</summary>
    private Task WaitAsync(TimeSpan delay, CancellationToken ct) =>
        Task.WhenAny(Volatile.Read(ref _sessionEnd).Task, Task.Delay(delay, ct));

    private static TaskCompletionSource<string> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override void Dispose()
    {
        _idleTimer.Dispose();
        base.Dispose();
    }
}

public sealed record ComPortInfo(string Name, string? Description);

internal static partial class PortNames
{
    /// <summary>Puertos COM del sistema ordenados por número (COM2 antes que COM10).</summary>
    public static List<string> Available() =>
        SerialPort.GetPortNames()
            .Select(p => p.Trim().ToUpperInvariant())
            .Distinct()
            .OrderBy(p => int.TryParse(p.AsSpan(3), out var n) ? n : int.MaxValue)
            .ThenBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Puertos con el nombre del dispositivo que muestra el Administrador de dispositivos
    /// (p. ej. "Dispositivo serie USB"), para distinguir la lectora de otros puertos.
    /// </summary>
    public static List<ComPortInfo> AvailableWithDescriptions()
    {
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (var device in searcher.Get())
            {
                using (device)
                {
                    var name = device["Name"] as string;
                    var match = name is null ? null : ComSuffix().Match(name);
                    if (match is { Success: true })
                        descriptions[match.Groups[1].Value] = name![..match.Index].Trim();
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Sin WMI se listan sólo los nombres.
        }

        return Available()
            .Select(p => new ComPortInfo(p, descriptions.GetValueOrDefault(p)))
            .ToList();
    }

    [GeneratedRegex(@"\((COM\d+)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ComSuffix();
}
