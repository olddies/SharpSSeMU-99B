using System.Net;
using System.Net.Sockets;
using MuServer.Shared.Logging;
using MuServer.Shared.Protocol;

namespace MuServer.GameServer.Net;

/// <summary> Port of CSocketManagerUdp in client mode + GameServerLiveProc() (GameServer.cpp): every 1s it
/// sends ConnectServer the 0xA1 heartbeat (SDHP_GAME_SERVER_LIVE_RECV) to announce itself alive and report
/// connected users. Without this ConnectServer never shows this GameServer in the list nor can it resolve its
/// IP:port for the client (see MuServer.ConnectServer/Data/ServerListStore.cs, which expires an entry after 10s
/// without heartbeat). </summary>
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
