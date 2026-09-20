using System.Net.Sockets;
using MuServer.GameServer.Protocol;
using MuServer.Shared.Logging;
using MuServer.Shared.Protocol;

namespace MuServer.GameServer.Net;

/// <summary>Puerto de la porción "cliente de JoinServer" de GameServer.cpp/JSProtocol.cpp
/// (JoinServerConnect/JoinServerMsgProc). Protocolo plano C1, sin cifrado (a diferencia del
/// socket de clientes reales) — igual patrón que usan ConnectServer/JoinServer/DataServer entre sí.</summary>
public sealed class JoinServerConnection
{
    private readonly string _address;
    private readonly ushort _port;
    private readonly string _serverName;
    private readonly ushort _serverPort;
    private readonly ushort _serverCode;
    private readonly Func<JoinAccountResultRecv, CancellationToken, Task> _onAccountResult;
    private readonly Func<DisconnectAccountAckRecv, CancellationToken, Task>? _onDisconnectAck;

    private Socket? _socket;
    private readonly PacketFramer _framer = new(maxPacketSize: 2048);
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected { get; private set; }

    public JoinServerConnection(string address, ushort port, string serverName, ushort serverPort, ushort serverCode,
        Func<JoinAccountResultRecv, CancellationToken, Task> onAccountResult,
        Func<DisconnectAccountAckRecv, CancellationToken, Task>? onDisconnectAck = null)
    {
        _address = address;
        _port = port;
        _serverName = serverName;
        _serverPort = serverPort;
        _serverCode = serverCode;
        _onAccountResult = onAccountResult;
        _onDisconnectAck = onDisconnectAck;
    }

    public void Start(CancellationToken ct) => _ = ConnectLoopAsync(ct);

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await _socket.ConnectAsync(_address, _port, ct);
                IsConnected = true;

                Log.Add(LogColor.Blue, "[JoinServer] Connected to {0}:{1}", _address, _port);

                await SendAsync(JoinServerClientPacketBuilder.ServerInfoSend(0, _serverPort, _serverName, _serverCode), ct);

                await ReceiveLoopAsync(ct);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                // reintenta abajo
            }
            catch (Exception ex)
            {
                Log.Add(LogColor.Red, "[JoinServer] Error: {0}", ex.Message);
            }

            IsConnected = false;
            _socket?.Close();

            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(5000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[2048];

        while (!ct.IsCancellationRequested)
        {
            int received = await _socket!.ReceiveAsync(buffer, SocketFlags.None, ct);

            if (received == 0)
            {
                break;
            }

            var packets = _framer.Feed(buffer.AsSpan(0, received));

            foreach (var packet in packets)
            {
                await DispatchAsync(packet, ct);
            }
        }
    }

    private async Task DispatchAsync(byte[] packet, CancellationToken ct)
    {
        byte head = packet[2];

        switch (head)
        {
            case 0x01:
                await _onAccountResult(JoinAccountResultRecv.Parse(packet), ct);
                break;

            case 0x02:
                // Puerto de JGDisconnectAccountRecv (JSProtocol.cpp:94-100): en el original, cierra
                // forzosamente el socket si el índice/cuenta siguen coincidiendo con un cliente
                // conectado (gObjIsAccountValid) -- una red de seguridad para el caso borde de que
                // JoinServer decida por su cuenta que la sesión ya no es válida. Este puerto ya cierra
                // la sesión de forma proactiva apenas el socket del cliente se desconecta de verdad
                // (ver ClientProtocolHandler.OnDisconnectAsync, que es lo que dispara el envío de este
                // mismo 0x02 hacia JoinServer en primer lugar), así que para cuando esta respuesta
                // llega la sesión casi siempre ya fue removida -- el cierre forzoso quedaría en la
                // práctica como no-op. Se deja el callback enganchado igual (por si a futuro se agrega
                // un cierre de sesión iniciado por JoinServer de verdad, ej. duplicidad de cuenta) en
                // vez de solo loguearlo como no implementado.
                if (_onDisconnectAck != null)
                {
                    await _onDisconnectAck(DisconnectAccountAckRecv.Parse(packet), ct);
                }

                break;

            default:
                Log.Add(LogColor.Black, "[JoinServer] Head 0x{0:X2} not handled yet", head);
                break;
        }
    }

    public async Task SendAsync(byte[] packet, CancellationToken ct)
    {
        if (_socket == null || !IsConnected)
        {
            return;
        }

        await _sendLock.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(packet, SocketFlags.None, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
