using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace SerialBridge;

/// <summary>
/// Clientes WebSocket conectados. Cada uno tiene su propia cola, así un front lento
/// no frena la lectura del puerto ni a los demás clientes.
/// </summary>
public sealed class WebSocketHub
{
    private const int MaxQueuedMessages = 256;

    private readonly ConcurrentDictionary<Guid, Channel<byte[]>> _clients = new();
    private readonly ILogger<WebSocketHub> _logger;

    public WebSocketHub(ILogger<WebSocketHub> logger) => _logger = logger;

    public int ClientCount => _clients.Count;

    public void Broadcast(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        foreach (var outbox in _clients.Values)
            outbox.Writer.TryWrite(bytes);
    }

    /// <summary>Atiende un cliente hasta que se desconecte o se detenga el servidor.</summary>
    public async Task HandleAsync(WebSocket socket, string? greeting, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxQueuedMessages)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        if (greeting is not null)
            outbox.Writer.TryWrite(Encoding.UTF8.GetBytes(greeting));

        _clients[id] = outbox;
        _logger.LogInformation("Cliente WebSocket conectado ({Count} en total)", _clients.Count);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var send = SendLoopAsync(socket, outbox.Reader, cts.Token);
            var receive = ReceiveLoopAsync(socket, cts.Token);
            await Task.WhenAny(send, receive);
            cts.Cancel();
            await Task.WhenAll(send, receive);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            outbox.Writer.TryComplete();
            _logger.LogInformation("Cliente WebSocket desconectado ({Count} en total)", _clients.Count);
        }

        await CloseQuietlyAsync(socket, ct.IsCancellationRequested);
    }

    private static async Task SendLoopAsync(WebSocket socket, ChannelReader<byte[]> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync(ct))
                await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
    }

    /// <summary>El front no necesita enviar nada; sólo se lee para detectar el cierre.</summary>
    private static async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket, bool serverStopping)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await socket.CloseOutputAsync(
                serverStopping ? WebSocketCloseStatus.EndpointUnavailable : WebSocketCloseStatus.NormalClosure,
                serverStopping ? "Servidor reiniciando" : null,
                timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
    }
}
