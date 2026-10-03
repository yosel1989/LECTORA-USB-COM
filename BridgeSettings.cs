using System.IO.Ports;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SerialBridge;

/// <summary>Configuración editable desde la página local o desde settings.json.</summary>
public sealed record BridgeSettings
{
    /// <summary>Único puerto COM del que se leen las tramas ("COM3"). Vacío = sin seleccionar.</summary>
    public string ComPort { get; init; } = "";
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public Parity Parity { get; init; } = Parity.None;
    public StopBits StopBits { get; init; } = StopBits.One;

    /// <summary>Puerto de salida del WebSocket (ws://127.0.0.1:&lt;puerto&gt;/).</summary>
    public int WebSocketPort { get; init; } = 21818;

    /// <summary>"json" envía {"type":"frame",...}; "raw" envía sólo el texto de la trama.</summary>
    public string OutputFormat { get; init; } = "json";

    /// <summary>Texto recibido sin CR/LF se emite como trama tras este silencio (ms). 0 = desactivado.</summary>
    public int FrameIdleTimeoutMs { get; init; } = 100;

    /// <summary>Orígenes web autorizados a conectarse al WebSocket. Vacío = cualquiera.</summary>
    public string[] AllowedOrigins { get; init; } = [];

    [JsonIgnore] public bool HasComPort => ComPort.Length > 0;
    [JsonIgnore] public bool IsRawOutput => OutputFormat.Equals("raw", StringComparison.OrdinalIgnoreCase);

    public BridgeSettings Normalize() => this with
    {
        // "AUTO" era el valor por defecto de la primera versión: ahora cuenta como "sin seleccionar".
        ComPort = (ComPort ?? "").Trim().ToUpperInvariant() is var port && port != "AUTO" ? port : "",
        OutputFormat = (OutputFormat ?? "").Trim().ToLowerInvariant(),
        AllowedOrigins = (AllowedOrigins ?? [])
            .Select(o => (o ?? "").Trim().TrimEnd('/'))
            .Where(o => o.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray(),
    };

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (HasComPort && !Regex.IsMatch(ComPort, @"^COM\d{1,3}$"))
            errors.Add($"'{ComPort}' no es un puerto COM válido.");
        if (BaudRate is < 110 or > 4_000_000)
            errors.Add("La velocidad (baudios) debe estar entre 110 y 4000000.");
        if (DataBits is < 5 or > 8)
            errors.Add("Los bits de datos deben estar entre 5 y 8.");
        if (!Enum.IsDefined(Parity))
            errors.Add("Paridad no válida.");
        if (StopBits == StopBits.None || !Enum.IsDefined(StopBits))
            errors.Add("Bits de parada no válidos.");
        if (WebSocketPort is < 1 or > 65535)
            errors.Add("El puerto WebSocket debe estar entre 1 y 65535.");
        if (OutputFormat is not ("json" or "raw"))
            errors.Add("El formato de salida debe ser json o raw.");
        if (FrameIdleTimeoutMs is < 0 or > 60_000)
            errors.Add("El tiempo de silencio debe estar entre 0 y 60000 ms.");
        return errors;
    }

    /// <summary>Sin lista blanca se acepta todo; la propia página local siempre está permitida.</summary>
    public bool IsOriginAllowed(string? origin, int listeningPort)
    {
        if (AllowedOrigins.Length == 0 || string.IsNullOrEmpty(origin))
            return true;
        origin = origin.TrimEnd('/');
        return origin.Equals($"http://127.0.0.1:{listeningPort}", StringComparison.OrdinalIgnoreCase)
            || origin.Equals($"http://localhost:{listeningPort}", StringComparison.OrdinalIgnoreCase)
            || AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions File = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
