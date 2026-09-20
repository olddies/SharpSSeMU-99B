using System.Net;
using System.Net.Sockets;
using MuServer.ConnectServer.Data;
using MuServer.Shared.Logging;
using MuServer.Shared.Protocol;

namespace MuServer.ConnectServer.Net;

/// <summary>
/// Puerto de CSocketManagerUdp (lado servidor): recibe por UDP los "heartbeat" que cada
/// GameServer (0xA1, cada pocos segundos) y el JoinServer (0xA2) envían para anunciarse vivos.
/// </summary>
public sealed class UdpHeartbeatServer
{
    private readonly ushort _port;
    private readonly ServerListStore _serverList;
    private readonly PacketFramer _framer = new(maxPacketSize: 4096);

    public UdpHeartbeatServer(ushort port, ServerListStore serverList)
    {
        _port = port;
        _serverList = serverList;
    }

    public void Start(CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, _port));

        Log.Add(LogColor.Black, "[SocketUDP] Server started at port [{0}]", _port);

        _ = ReceiveLoopAsync(socket, ct);
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested)
        {
            int received;

            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
                received = result.ReceivedBytes;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Log.Add(LogColor.Red, "[SocketUDP] recvfrom() failed with error: {0}", ex.SocketErrorCode);
                continue;
            }

            List<byte[]> packets;

            try
            {
                packets = _framer.Feed(buffer.AsSpan(0, received));
            }
            catch (InvalidDataException ex)
            {
                Log.Add(LogColor.Red, "[SocketUDP] {0}", ex.Message);
                continue;
            }

            foreach (var packet in packets)
            {
                Dispatch(packet);
            }
        }
    }

    private void Dispatch(byte[] packet)
    {
        if (packet.Length < 3)
        {
            return;
        }

        byte head = packet[2];

        switch (head)
        {
            case 0xA1: // SDHP_GAME_SERVER_LIVE_RECV: header(3) + DWORD ServerCode + DWORD UserCount + DWORD UserTotal
                if (packet.Length < 15)
                {
                    return;
                }

                int serverCode = BitConverter.ToInt32(packet, 3);
                uint userCount = BitConverter.ToUInt32(packet, 7);
                uint userTotal = BitConverter.ToUInt32(packet, 11);

                _serverList.OnGameServerLive(serverCode, userCount, userTotal);
                break;

            case 0xA2: // SDHP_JOIN_SERVER_LIVE_RECV: header(3) + DWORD QueueSize
                if (packet.Length < 7)
                {
                    return;
                }

                uint queueSize = BitConverter.ToUInt32(packet, 3);

                _serverList.OnJoinServerLive(queueSize);
                break;
        }
    }
}
