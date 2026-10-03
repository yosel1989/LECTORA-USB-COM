using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace SerialBridge;

/// <summary>
/// Servidor Kestrel en 127.0.0.1:&lt;WebSocketPort&gt;. Expone el WebSocket, la página de
/// configuración y su API. Si cambia el puerto en la configuración, se reinicia en el nuevo.
/// </summary>
public sealed class WebServerHost : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly Lazy<string> IndexHtml = new(LoadIndexHtml);

    private readonly SettingsStore _store;
    private readonly WebSocketHub _hub;
    private readonly SerialReaderService _serial;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WebServerHost> _logger;

    public WebServerHost(SettingsStore store, WebSocketHub hub, SerialReaderService serial, ILoggerFactory loggerFactory)
    {
        _store = store;
        _hub = hub;
        _serial = serial;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WebServerHost>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int? lastFailedPort = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var port = _store.Current.WebSocketPort;
            var restart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(BridgeSettings previous, BridgeSettings current)
            {
                if (previous.WebSocketPort != current.WebSocketPort)
                    restart.TrySetResult();
            }

            _store.Changed += OnChanged;
            WebApplication? app = null;
            try
            {
                app = BuildApp(port);
                await app.StartAsync(stoppingToken);
                lastFailedPort = null;
                _logger.LogInformation("WebSocket en ws://127.0.0.1:{Port}/ · configuración en http://127.0.0.1:{Port}/", port, port);
                await Task.WhenAny(restart.Task, Task.Delay(Timeout.Infinite, stoppingToken));
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                if (lastFailedPort != port)
                {
                    _logger.LogError("No se pudo escuchar en 127.0.0.1:{Port}: {Error}. Se reintentará cada {Seconds}s; " +
                        "para usar otro puerto edite WebSocketPort en {File}", port, ex.Message, RetryDelay.TotalSeconds, _store.FilePath);
                    lastFailedPort = port;
                }
                await Task.WhenAny(restart.Task, Task.Delay(RetryDelay, stoppingToken));
            }
            finally
            {
                _store.Changed -= OnChanged;
                if (app is not null)
                    await StopAppAsync(app);
            }
        }
    }

    private WebApplication BuildApp(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });

        // Los logs de Kestrel van a los mismos destinos que el servicio (consola / Visor de eventos).
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_loggerFactory);

        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        app.Map("/", ctx => ctx.WebSockets.IsWebSocketRequest ? AcceptWebSocketAsync(ctx, port) : ServeIndexAsync(ctx));
        app.Map("/ws", ctx => AcceptWebSocketAsync(ctx, port));

        app.MapGet("/api/status", () => new
        {
            serial = _serial.Status,
            clients = _hub.ClientCount,
            webSocketUrl = $"ws://127.0.0.1:{port}/",
            settingsFile = _store.FilePath,
        });

        app.MapGet("/api/ports", () => PortNames.AvailableWithDescriptions());

        app.MapGet("/api/settings", () => _store.Current);

        // Al exigir JSON, un sitio web ajeno no puede cambiar la configuración: el navegador
        // pide un preflight CORS y este servidor no lo autoriza.
        app.MapPut("/api/settings", (BridgeSettings settings) =>
        {
            var previousPort = _store.Current.WebSocketPort;
            var errors = settings.Normalize().Validate();
            if (errors.Count == 0 && settings.WebSocketPort != previousPort && !IsPortFree(settings.WebSocketPort))
                errors.Add($"El puerto {settings.WebSocketPort} ya está en uso por otro programa.");
            if (errors.Count == 0)
                errors = _store.TryUpdate(settings);
            if (errors.Count > 0)
                return Results.BadRequest(new { errors });

            var current = _store.Current;
            return Results.Ok(new { settings = current, webSocketPortChanged = current.WebSocketPort != previousPort });
        });

        // Reinicio pedido desde el icono de la barra de tareas (que corre sin permisos de administrador).
        // El proceso termina con código de error y Windows lo vuelve a iniciar según las acciones de
        // recuperación del servicio (configuradas por el instalador). Exigir JSON impide que un sitio
        // web ajeno lo dispare: el navegador pediría un preflight CORS que este servidor no autoriza.
        app.MapPost("/api/service/restart", (HttpContext ctx) =>
        {
            if (!ctx.Request.HasJsonContentType())
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            if (!WindowsServiceHelpers.IsWindowsService())
                return Results.Conflict(new { errors = new[] { "El reinicio solo está disponible cuando Lector USB corre como servicio de Windows." } });

            _logger.LogWarning("Reinicio del servicio solicitado desde el icono de la barra de tareas");
            _ = Task.Run(async () =>
            {
                await Task.Delay(500); // deja salir la respuesta
                Environment.Exit(1);
            });
            return Results.Accepted();
        });

        return app;
    }

    private async Task AcceptWebSocketAsync(HttpContext ctx, int port)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("Se esperaba una conexión WebSocket.");
            return;
        }

        var settings = _store.Current;
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!settings.IsOriginAllowed(origin, port))
        {
            _logger.LogWarning("Conexión WebSocket rechazada desde el origen {Origin}", origin);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var stopping = ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping);
        await _hub.HandleAsync(socket, settings.IsRawOutput ? null : _serial.BuildStatusMessage(), cts.Token);
    }

    private static Task ServeIndexAsync(HttpContext ctx)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method))
        {
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return Task.CompletedTask;
        }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        return ctx.Response.WriteAsync(IndexHtml.Value);
    }

    private async Task StopAppAsync(WebApplication app)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await app.StopAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error al detener el servidor web");
        }
        await app.DisposeAsync();
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string LoadIndexHtml()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SerialBridge.index.html")
            ?? throw new InvalidOperationException("Falta el recurso embebido index.html");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
