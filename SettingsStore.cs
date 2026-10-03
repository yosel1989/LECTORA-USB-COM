using System.Text.Json;

namespace SerialBridge;

/// <summary>
/// Persiste la configuración en %ProgramData%\SerialBridge\settings.json y avisa de los cambios,
/// tanto los hechos desde la página como las ediciones manuales del archivo.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private readonly object _lock = new();
    private readonly ILogger<SettingsStore> _logger;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _reloadTimer;
    private BridgeSettings _current;

    public SettingsStore(ILogger<SettingsStore> logger)
    {
        _logger = logger;
        FilePath = DefaultFilePath;
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);

        _current = ReadFile() ?? new BridgeSettings();
        if (!File.Exists(FilePath))
            WriteFile(_current);

        // Las ediciones manuales suelen disparar varios eventos seguidos: se agrupan en uno.
        _reloadTimer = new Timer(_ => ReloadFromDisk());
        _watcher = new FileSystemWatcher(dir, "settings.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += (_, _) => _reloadTimer.Change(500, Timeout.Infinite);
        _watcher.Created += (_, _) => _reloadTimer.Change(500, Timeout.Infinite);
        _watcher.Renamed += (_, _) => _reloadTimer.Change(500, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
    }

    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SerialBridge", "settings.json");

    public string FilePath { get; }

    public BridgeSettings Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>(anterior, nueva)</summary>
    public event Action<BridgeSettings, BridgeSettings>? Changed;

    /// <summary>Valida, guarda y aplica. Devuelve la lista de errores (vacía si se guardó).</summary>
    public List<string> TryUpdate(BridgeSettings settings)
    {
        settings = settings.Normalize();
        var errors = settings.Validate();
        if (errors.Count > 0)
            return errors;

        WriteFile(settings);
        Apply(settings);
        return errors;
    }

    private void Apply(BridgeSettings settings)
    {
        BridgeSettings previous;
        lock (_lock)
        {
            if (SameAs(_current, settings))
                return;
            previous = _current;
            _current = settings;
        }

        _logger.LogInformation("Configuración actualizada: {Settings}", JsonSerializer.Serialize(settings, Json.Wire));
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<BridgeSettings, BridgeSettings>)handler)(previous, settings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al aplicar la nueva configuración");
            }
        }
    }

    private void ReloadFromDisk()
    {
        var settings = ReadFile();
        if (settings is not null)
            Apply(settings);
    }

    private BridgeSettings? ReadFile()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            var settings = JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(FilePath), Json.File)?.Normalize();
            if (settings is null)
                return null;

            var errors = settings.Validate();
            if (errors.Count > 0)
            {
                _logger.LogWarning("Se ignora {File}: {Errors}", FilePath, string.Join(" ", errors));
                return null;
            }
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("No se pudo leer {File}: {Error}", FilePath, ex.Message);
            return null;
        }
    }

    private void WriteFile(BridgeSettings settings)
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json.File));
        File.Move(tmp, FilePath, overwrite: true);
    }

    private static bool SameAs(BridgeSettings a, BridgeSettings b) =>
        JsonSerializer.Serialize(a, Json.Wire) == JsonSerializer.Serialize(b, Json.Wire);

    public void Dispose()
    {
        _watcher.Dispose();
        _reloadTimer.Dispose();
    }
}
