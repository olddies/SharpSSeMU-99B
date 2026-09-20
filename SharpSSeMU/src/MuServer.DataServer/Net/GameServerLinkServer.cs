using System.Net;
using System.Net.Sockets;
using MuServer.DataServer.Data;
using MuServer.DataServer.Protocol;
using MuServer.Shared.Logging;

namespace MuServer.DataServer.Net;

/// <summary> Port of CSocketManager (DataServer side): accepts incoming TCP connections from authorised
/// GameServers (AllowableIpList.txt). Like JoinServer, no rate-limit/idle-timeout (trusted connections, maximum
/// 20 = MAX_SERVER). No UDP heartbeat: DataServer does not talk to ConnectServer. </summary>
public sealed class GameServerLinkServer
{
    private readonly ushort _port;
    private readonly AllowableIpStore _allowList;
    private readonly GameServerRegistry _registry;
    private readonly CharacterSessionStore _sessions;
    private readonly DataServerProtocolHandler _protocol;

    private Socket? _listener;

    public GameServerLinkServer(ushort port, AllowableIpStore allowList, GameServerRegistry registry, CharacterSessionStore sessions, DataServerProtocolHandler protocol)
    {
        _port = port;
        _allowList = allowList;
        _registry = registry;
        _sessions = sessions;
        _protocol = protocol;
    }

    public void Start(CancellationToken ct)
    {
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Any, _port));
        _listener.Listen(5);

        Log.Add(LogColor.Black, "[SocketTCP] Server started at port [{0}]", _port);

        _ = AcceptLoopAsync(ct);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;

            try
            {
                socket = await _listener!.AcceptAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Log.Add(LogColor.Red, "[SocketTCP] AcceptAsync() failed with error: {0}", ex.SocketErrorCode);
                continue;
            }

            _ = HandleGameServerAsync(socket, ct);
        }
    }

    private async Task HandleGameServerAsync(Socket socket, CancellationToken ct)
    {
        var remoteIp = (socket.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "0.0.0.0";

        if (!_allowList.IsAllowed(remoteIp))
        {
            socket.Close();
            return;
        }

        var link = new GameServerLink { Socket = socket, IpAddress = remoteIp };

        if (!_registry.TryAdd(link))
        {
            socket.Close();
            return;
        }

        var buffer = new byte[8192];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int received;

                try
                {
                    received = await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
                }
                catch (SocketException ex)
                {
                    Log.Add(LogColor.Red, "[SocketTCP] ReceiveAsync() failed: {0}", ex);
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (received == 0)
                {
                    break;
                }

                List<byte[]> packets;

                try
                {
                    packets = link.Framer.Feed(buffer.AsSpan(0, received));
                }
                catch (InvalidDataException ex)
                {
                    Log.Add(LogColor.Red, "[SocketTCP] {0} (IpAddress: {1})", ex.Message, link.IpAddress);
                    break;
                }

                foreach (var packet in packets)
                {
                    await _protocol.HandlePacketAsync(link, packet, ct);
                }
            }
        }
        finally
        {
            _registry.Remove(link);

            // Port of ClearServerCharacterInfo: when the GameServer goes down, its online characters are cleared.
            if (link.ServerCode != 0xFFFF)
            {
                _sessions.ClearByServerCode(link.ServerCode);
            }

            socket.Close();
        }
    }
}
