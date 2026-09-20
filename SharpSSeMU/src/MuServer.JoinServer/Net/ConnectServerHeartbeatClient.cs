using System.Net;
using System.Net.Sockets;
using MuServer.Shared.Logging;
using MuServer.Shared.Protocol;

namespace MuServer.JoinServer.Net;

/// <summary>
/// Puerto de CSocketManagerUdp en modo cliente + JoinServerLiveProc(): cada 1s manda al
/// ConnectServer el heartbeat 0xA2 (SDHP_JOIN_SERVER_LIVE_RECV) para anunciarse vivo.
/// </summary>
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

            // QueueSize: el original reportaba el tamaño de su cola interna de paquetes pendientes
            // de procesar; acá no existe esa cola (se procesa en línea), así que se reporta 0.
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
