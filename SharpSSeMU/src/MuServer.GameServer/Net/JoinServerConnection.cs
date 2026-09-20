using System.Net.Sockets;
using MuServer.GameServer.Protocol;
using MuServer.Shared.Logging;
using MuServer.Shared.Protocol;

namespace MuServer.GameServer.Net;

/// <summary>Port of the "JoinServer client" portion of GameServer.cpp/JSProtocol.cpp
/// (JoinServerConnect/JoinServerMsgProc). Plain C1 protocol, without encryption (unlike the real clients'
/// socket) — the same pattern ConnectServer/JoinServer/DataServer use among themselves.</summary>
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
                // Port of JGDisconnectAccountRecv (JSProtocol.cpp:94-100): in the original, it forcibly closes
                // the socket if the index/account still match a connected client (gObjIsAccountValid) -- a
                // safety net for the edge case where JoinServer decides on its own that the session is no
                // longer valid. This port already closes the session proactively as soon as the client's socket
                // really disconnects (see ClientProtocolHandler.OnDisconnectAsync, which is what triggers
                // sending this same 0x02 to JoinServer in the first place), so by the time this reply arrives
                // the session has almost always already been removed -- the forced close would be a no-op in
                // practice. The callback is left hooked anyway (in case a truly JoinServer-initiated session
                // close, e.g. duplicate account, is added in the future) instead of just logging it as not
                // implemented.
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
