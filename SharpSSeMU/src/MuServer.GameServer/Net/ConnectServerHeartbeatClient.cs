using System.Net;
using System.Net.Sockets;
using MuServer.Shared.Logging;
using MuServer.Shared.Protocol;

namespace MuServer.GameServer.Net;

/// <summary>
/// Puerto de CSocketManagerUdp en modo cliente + GameServerLiveProc() (GameServer.cpp): cada 1s
/// manda a ConnectServer el heartbeat 0xA1 (SDHP_GAME_SERVER_LIVE_RECV) para anunciarse vivo y
/// reportar usuarios conectados. Sin esto ConnectServer nunca muestra este GameServer en la lista
/// ni puede resolver su IP:puerto para el cliente (ver MuServer.ConnectServer/Data/ServerListStore.cs,
/// que expira una entrada a los 10s sin heartbeat).
/// </summary>
public sealed class GameServerHeartbeatClient
{
    private readonly string _address;
    private readonly ushort _port;
    private readonly ushort _serverCode;
    private readonly Func<int> _getUserCount;
    private readonly int _maxUserNumber;

    private Socket? _socket;

    public GameServerHeartbeatClient(string address, ushort port, ushort serverCode, int maxUserNumber, Func<int> getUserCount)
    {
        _address = address;
        _port = port;
        _serverCode = serverCode;
        _maxUserNumber = maxUserNumber;
        _getUserCount = getUserCount;
    }

    public void Start(CancellationToken ct)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Connect(IPAddress.Parse(_address), _port);

        _ = HeartbeatLoopAsync(ct);
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var payload = new byte[12];
            PacketBuilder.WriteUInt32LE(_serverCode).CopyTo(payload, 0);
            PacketBuilder.WriteUInt32LE((uint)_getUserCount()).CopyTo(payload, 4);
            PacketBuilder.WriteUInt32LE((uint)_maxUserNumber).CopyTo(payload, 8);

            var packet = PacketBuilder.BuildC1(0xA1, payload);

            try
            {
                _socket!.Send(packet);
            }
            catch (SocketException ex)
            {
                Log.Add(LogColor.Red, "[SocketUDP] sendto() failed with error: {0}", ex.SocketErrorCode);
            }
        }
    }
}
