using System.Net;
using System.Net.Sockets;
using MuServer.JoinServer.Data;
using MuServer.JoinServer.Protocol;
using MuServer.Shared.Logging;

namespace MuServer.JoinServer.Net;

/// <summary> Port of CSocketManager (JoinServer side): accepts incoming TCP connections from authorised
/// GameServers (AllowableIpList.txt) and dispatches their protocol. Unlike ConnectServer, here there is no
/// rate-limiting nor idle-timeout in the original — there are few trusted connections (maximum 20, MAX_SERVER),
/// so those controls are omitted as in the original C++. </summary>
public sealed class GameServerLinkServer
{
    private readonly ushort _port;
    private readonly AllowableIpStore _allowList;
    private readonly GameServerRegistry _registry;
    private readonly JoinServerProtocolHandler _protocol;

    private Socket? _listener;

    public GameServerLinkServer(ushort port, AllowableIpStore allowList, GameServerRegistry registry, JoinServerProtocolHandler protocol)
    {
        _port = port;
        _allowList = allowList;
        _registry = registry;
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

        var buffer = new byte[2048];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int received;

                try
                {
                    received = await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
                }
                catch (SocketException)
                {
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
            await _protocol.ClearServerAccountsAsync(link, CancellationToken.None);
            socket.Close();
        }
    }
}
