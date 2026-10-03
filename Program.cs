using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using SerialBridge;

// Servicio de Windows + WMI (nombres de los puertos COM): sólo Windows.
[assembly: SupportedOSPlatform("windows")]

// SerialBridge.exe --set-port <n>: cambia el puerto del WebSocket y termina (lo usa el instalador).
if (args is ["--set-port", var portArg])
    return SetPort(portArg);

// SerialBridge.exe --tray: icono en la barra de tareas (se inicia con la sesión de cada usuario).
if (args is ["--tray"])
    return TrayApp.Run();

// LECTOR -> USB (COM virtual) -> SerialPort.DataReceived -> broadcast WebSocket ws://127.0.0.1:<puerto>
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Como servicio, el directorio actual es System32; appsettings.json vive junto al .exe.
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddWindowsService(options => options.ServiceName = "SerialBridge");

builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<WebSocketHub>();
builder.Services.AddSingleton<SerialReaderService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SerialReaderService>());
builder.Services.AddHostedService<WebServerHost>();

builder.Build().Run();
return 0;

static int SetPort(string value)
{
    if (!int.TryParse(value, out var port))
    {
        Console.Error.WriteLine($"'{value}' no es un número de puerto válido.");
        return 2;
    }

    using var store = new SettingsStore(NullLogger<SettingsStore>.Instance);
    var errors = store.TryUpdate(store.Current with { WebSocketPort = port });
    foreach (var error in errors)
        Console.Error.WriteLine(error);
    if (errors.Count > 0)
        return 1;

    Console.WriteLine($"Puerto del WebSocket: {port} ({store.FilePath})");
    return 0;
}
