using System.Net;
using System.Net.Sockets;
using MuServer.Shared.Logging;
using MuServer.Shared.Protocol;

namespace MuServer.JoinServer.Net;

/// <summary> Port of CSocketManagerUdp in client mode + JoinServerLiveProc(): every 1s it sends the
/// ConnectServer the 0xA2 heartbeat (SDHP_JOIN_SERVER_LIVE_RECV) to announce itself alive. </summary>
public sealed class ConnectServerHeartbeatClient
{
    private readonly string _address;
    private readonly ushort _port;
    private Socket? _socket;

    public ConnectServerHeartbeatClient(string address, ushort port)
    {
        _address = address;
        _port = port;
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

            // QueueSize: the original reported the size of its internal queue of packets pending processing;
            // here that queue does not exist (processing is inline), so 0 is reported.
            var payload = PacketBuilder.WriteUInt32LE(0);
            var packet = PacketBuilder.BuildC1(0xA2, payload);

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
